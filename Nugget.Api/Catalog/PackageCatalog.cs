using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nugget.Api.Packages;

namespace Nugget.Api.Catalog;

public sealed class SearchRequest {
  public string? Query { get; init; }
  public int Skip { get; init; }
  public int Take { get; init; } = 20;
  public bool Prerelease { get; init; }
  public bool SemVer2 { get; init; }
  public string? PackageType { get; init; }
}

public sealed class SearchHit {
  public required PackageSummary Package { get; init; }
  public required SummaryVersion Latest { get; init; }
  public required SearchDetails Details { get; init; }
}

public sealed class SearchResults {
  public required int TotalHits { get; init; }
  public required IReadOnlyList<SearchHit> Hits { get; init; }
}

/// <summary>
///   All package IDs and versions in memory. It answers search and
///   autocomplete, and lets the flat container answer version lists and
///   unknown packages without touching S3.
/// </summary>
public sealed class PackageCatalog(DownloadStatistics downloads) {
  private static readonly TimeSpan S_BROWSE_CACHE_TTL_ =
    TimeSpan.FromSeconds(30);

  private static readonly TimeSpan S_BROWSE_CACHE_MIN_AGE_ =
    TimeSpan.FromSeconds(10);

  private static readonly IComparer<Match> S_WORST_FIRST_ =
    Comparer<Match>.Create((a, b) => Compare(b, a));

  private static readonly char[] S_ID_SEPARATORS_ = [
    '.',
    '-',
    '_'
  ];

  private readonly ConcurrentDictionary<string, BrowseCache> browse_cache_ =
    new(StringComparer.Ordinal);

  private readonly ConcurrentDictionary<string, PackageSummary> packages_ =
    new(StringComparer.Ordinal);

  private readonly TaskCompletionSource ready_ =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  private ArraySnapshot? array_;
  private long generation_;

  /// <summary>
  ///   Whether the catalog has been loaded. Before that, "not in the catalog"
  ///   does not mean "does not exist".
  /// </summary>
  public bool IsReady => ready_.Task.IsCompleted;

  public Task WhenReady => ready_.Task;

  public int Count => packages_.Count;

  public void MarkReady() {
    ready_.TrySetResult();
  }

  public void Load(IEnumerable<PackageSummary> summaries) {
    packages_.Clear();
    foreach (PackageSummary summary in summaries) {
      packages_[summary.LowerId] = summary;
    }

    Changed();
  }

  /// <summary>
  ///   Updates one package from its metadata document (null = deleted).
  ///   Documents older than the one already applied are ignored, so racing
  ///   local and remote updates cannot go back in time.
  /// </summary>
  public void Apply(string lower_id, PackageRecord? record) {
    if (record == null || record.Versions.Count == 0) {
      packages_.TryRemove(lower_id, out _);
    } else {
      PackageSummary summary = PackageSummary.FromRecord(record);
      packages_.AddOrUpdate(
        lower_id,
        summary,
        (_, existing) => existing.Revision > summary.Revision
                           ? existing
                           : summary
      );
    }

    Changed();
  }

  public PackageSummary? Find(string lower_id) {
    return packages_.GetValueOrDefault(lower_id);
  }

  /// <summary>
  ///   <c>false</c> only when the catalog is loaded and does not know the
  ///   package, so a request can be answered with 404 without asking S3.
  /// </summary>
  public bool MayExist(string lower_id) {
    return !IsReady || packages_.ContainsKey(lower_id);
  }

  public IReadOnlyList<PackageSummary> GetAll() {
    return GetArray();
  }

  public SearchResults Search(SearchRequest request) {
    SearchQuery query = SearchQuery.Parse(request.Query);
    int skip = Math.Max(0, request.Skip);
    int wanted = (int)Math.Min(
      int.MaxValue,
      (long)skip + Math.Max(0, request.Take)
    );
    List<Match> matches;
    List<Match> top;
    if (query.IsEmpty) {
      matches = Browse(request);
      top = matches;
    } else {
      matches = FindMatches(request, query);
      top = Top(matches, wanted);
    }

    List<SearchHit> page = [];
    for (int i = skip; i < Math.Min(wanted, top.Count); i++) {
      Match match = top[i];
      page.Add(
        new SearchHit {
          Package = match.Package,
          Latest = match.Latest,
          Details = match.Details
        }
      );
    }

    return new SearchResults {
      TotalHits = matches.Count,
      Hits = page
    };
  }

  /// <summary>Package IDs starting with (a word of) <paramref name="query" />.</summary>
  public (int TotalHits, List<string> Ids) AutocompleteIds(string? query,
    int skip, int take, bool prerelease, bool semver2,
    string? package_type) {
    string q = (query ?? "").Trim();
    List<Match> matches = [];
    foreach (PackageSummary package in GetArray()) {
      if (!TryGetLatest(
            package,
            prerelease,
            semver2,
            package_type,
            out SummaryVersion? latest,
            out SearchDetails? details
          )) {
        continue;
      }

      int score = q.Length == 0 ? 1 : ScoreAutocomplete(package.Id, q);
      if (score > 0) {
        matches.Add(
          new Match(
            package,
            latest,
            details,
            score,
            downloads.GetTotal(package.LowerId)
          )
        );
      }
    }

    List<Match> top = Top(matches, Math.Max(0, skip) + Math.Max(0, take));
    List<string> ids = [];
    for (int i = Math.Max(0, skip); i < top.Count; i++) {
      ids.Add(top[i].Package.Id);
    }

    return (matches.Count, ids);
  }

  /// <summary>Listed versions of one package, for version autocomplete.</summary>
  public List<string> AutocompleteVersions(string lower_id, bool prerelease,
                                           bool semver2) {
    PackageSummary? package = Find(lower_id);
    return package == null
             ? []
             : package.GetListed(prerelease, semver2)
                      .Select(v => v.Version)
                      .ToList();
  }

  /// <summary>Every package matching the filters, most downloaded first.</summary>
  private List<Match> Browse(SearchRequest request) {
    // Visual Studio's Browse tab sends an empty query every time it opens,
    // so the sorted list is reused: until something changes (at most for
    // the TTL, as download counts move), and while packages are streaming
    // in, for a few seconds at a time.
    string cache_key = $"{request.Prerelease}|{request.SemVer2}|" +
                       $"{request.PackageType?.ToLowerInvariant()}";
    long generation = Interlocked.Read(ref generation_);
    if (browse_cache_.TryGetValue(cache_key, out BrowseCache? cached)) {
      TimeSpan age = DateTimeOffset.UtcNow - cached.Created;
      if ((cached.Generation == generation && age < S_BROWSE_CACHE_TTL_) ||
          age < S_BROWSE_CACHE_MIN_AGE_) {
        return cached.Matches;
      }
    }

    List<Match> matches = [];
    foreach (PackageSummary package in GetArray()) {
      if (TryGetLatest(
            package,
            request.Prerelease,
            request.SemVer2,
            request.PackageType,
            out SummaryVersion? latest,
            out SearchDetails? details
          )) {
        matches.Add(
          new Match(
            package,
            latest,
            details,
            0,
            downloads.GetTotal(package.LowerId)
          )
        );
      }
    }

    matches.Sort(Compare);
    browse_cache_[cache_key] = new BrowseCache {
      Generation = generation,
      Created = DateTimeOffset.UtcNow,
      Matches = matches
    };
    return matches;
  }

  private List<Match> FindMatches(SearchRequest request, SearchQuery query) {
    PackageSummary[] packages = GetArray();
    List<Match> matches = [];
    Lock gate = new();
    // A linear scan: a few milliseconds per 100,000 packages and core, and
    // it supports substring matches without maintaining an index.
    Parallel.For(
      0,
      packages.Length,
      new ParallelOptions {
        MaxDegreeOfParallelism = packages.Length > 20_000
                                   ? Environment.ProcessorCount
                                   : 1
      },
      () => new List<Match>(),
      (index, _, local) => {
        PackageSummary package = packages[index];
        if (!TryGetLatest(
              package,
              request.Prerelease,
              request.SemVer2,
              request.PackageType,
              out SummaryVersion? latest,
              out SearchDetails? details
            )) {
          return local;
        }

        double score = Relevance(query, package, details);
        if (score > 0) {
          long total = downloads.GetTotal(package.LowerId);
          // Popular packages float up, but text relevance dominates.
          local.Add(
            new Match(
              package,
              latest,
              details,
              score * (1 + Math.Log10(1 + total) / 8),
              total
            )
          );
        }

        return local;
      },
      local => {
        lock (gate) {
          matches.AddRange(local);
        }
      }
    );
    return matches;
  }

  /// <summary>
  ///   The best <paramref name="count" /> matches in order. Pages are small,
  ///   so a bounded heap beats sorting tens of thousands of matches.
  /// </summary>
  private static List<Match> Top(List<Match> matches, int count) {
    if (count >= matches.Count / 4) {
      matches.Sort(Compare);
      return matches;
    }

    // The heap's root is the worst of the best matches kept so far.
    PriorityQueue<Match, Match> heap = new(count + 1, S_WORST_FIRST_);
    foreach (Match match in matches) {
      if (heap.Count < count) {
        heap.Enqueue(match, match);
      } else if (count > 0 && Compare(match, heap.Peek()) < 0) {
        heap.EnqueueDequeue(match, match);
      }
    }

    List<Match> top = new(heap.Count);
    while (heap.TryDequeue(out Match match, out _)) {
      top.Add(match);
    }

    top.Reverse();
    return top;
  }

  private static int Compare(Match a, Match b) {
    int result = b.Score.CompareTo(a.Score);
    if (result == 0) {
      result = b.Downloads.CompareTo(a.Downloads);
    }

    return result != 0
             ? result
             : string.Compare(
               a.Package.Id,
               b.Package.Id,
               StringComparison.OrdinalIgnoreCase
             );
  }

  private static bool TryGetLatest(PackageSummary package, bool prerelease,
                                   bool semver2, string? package_type,
                                   [NotNullWhen(true)]
                                   out SummaryVersion? latest,
                                   [NotNullWhen(true)]
                                   out SearchDetails? details) {
    details = null;
    latest = package.GetLatest(prerelease, semver2);
    if (latest == null) {
      return false;
    }

    details = package.GetDetails(latest);
    return details != null && MatchesPackageType(details, package_type);
  }

  private static bool MatchesPackageType(SearchDetails details,
                                         string? package_type) {
    if (string.IsNullOrWhiteSpace(package_type)) {
      return true;
    }

    if (details.PackageTypes.Length == 0) {
      // Packages without explicit types are "Dependency" packages.
      return package_type.Equals(
        "Dependency",
        StringComparison.OrdinalIgnoreCase
      );
    }

    foreach (string type in details.PackageTypes) {
      if (type.Equals(package_type, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>Relevance of a package; 0 when any query term does not match.</summary>
  private static double Relevance(SearchQuery query, PackageSummary package,
                                  SearchDetails details) {
    double total = 0;
    foreach (SearchTerm term in query.Terms) {
      double score = ScoreTerm(term, package, details);
      if (score <= 0) {
        return 0;
      }

      total += score;
    }

    if (package.Id.Equals(query.Text, StringComparison.OrdinalIgnoreCase)) {
      total += 1000;
    }

    return total;
  }

  private static double ScoreTerm(SearchTerm term, PackageSummary package,
                                  SearchDetails details) {
    string value = term.Value;
    switch (term.Field) {
      case SearchField.PACKAGE_ID:
        return package.Id.Equals(value, StringComparison.OrdinalIgnoreCase)
                 ? 100
                 : 0;
      case SearchField.ID:
        return ScoreId(package.Id, value);
      case SearchField.VERSION:
        foreach (SummaryVersion version in package.Versions) {
          if (version.Listed &&
              version.LowerVersion.Equals(
                value,
                StringComparison.OrdinalIgnoreCase
              )) {
            return 1;
          }
        }

        return 0;
      case SearchField.TITLE:
        return Contains(details.Title, value) ? 10 : 0;
      case SearchField.DESCRIPTION:
        return Contains(details.Description, value) ? 2 : 0;
      case SearchField.SUMMARY:
        return Contains(details.Summary, value) ? 2 : 0;
      case SearchField.TAGS:
        return ScoreTags(details.Tags, value);
      case SearchField.AUTHORS:
        return ContainsAny(details.Authors, value) ? 3 : 0;
      case SearchField.ANY:
      default: {
        double score = ScoreId(package.Id, value);
        if (details.Title != null &&
            details.Title.Equals(value, StringComparison.OrdinalIgnoreCase)) {
          score += 20;
        } else if (Contains(details.Title, value)) {
          score += 8;
        }

        score += ScoreTags(details.Tags, value);
        if (ContainsAny(details.Authors, value)) {
          score += 3;
        }

        if (Contains(details.Summary, value) ||
            Contains(details.Description, value)) {
          score += 2;
        }

        return score;
      }
    }
  }

  private static double ScoreId(string id, string value) {
    if (id.Equals(value, StringComparison.OrdinalIgnoreCase)) {
      return 100;
    }

    if (id.StartsWith(value, StringComparison.OrdinalIgnoreCase)) {
      return 40;
    }

    if (IdWordStartsWith(id, value)) {
      return 30;
    }

    return id.Contains(value, StringComparison.OrdinalIgnoreCase) ? 15 : 0;
  }

  private static double ScoreTags(string[] tags, string value) {
    double score = 0;
    foreach (string tag in tags) {
      if (tag.Equals(value, StringComparison.OrdinalIgnoreCase)) {
        return 10;
      }

      if (Contains(tag, value)) {
        score = 4;
      }
    }

    return score;
  }

  private static bool ContainsAny(string[] texts, string value) {
    foreach (string text in texts) {
      if (Contains(text, value)) {
        return true;
      }
    }

    return false;
  }

  private static int ScoreAutocomplete(string id, string query) {
    if (id.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
      return 2;
    }

    return IdWordStartsWith(id, query) ? 1 : 0;
  }

  /// <summary>Whether a dot/dash separated word of the ID starts with the value.</summary>
  private static bool IdWordStartsWith(string id, string value) {
    int index = id.IndexOfAny(S_ID_SEPARATORS_);
    while (index >= 0 && index + 1 < id.Length) {
      if (id.AsSpan(index + 1)
            .StartsWith(value, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }

      index = id.IndexOfAny(S_ID_SEPARATORS_, index + 1);
    }

    return false;
  }

  private static bool Contains(string? text, string value) {
    return text != null &&
           text.Contains(value, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>All packages as an array, rebuilt only after changes.</summary>
  private PackageSummary[] GetArray() {
    long generation = Interlocked.Read(ref generation_);
    ArraySnapshot? current = Volatile.Read(ref array_);
    if (current != null && current.Generation == generation) {
      return current.Packages;
    }

    PackageSummary[] packages = packages_.Values.ToArray();
    Volatile.Write(
      ref array_,
      new ArraySnapshot {
        Generation = generation,
        Packages = packages
      }
    );
    return packages;
  }

  private void Changed() {
    Interlocked.Increment(ref generation_);
  }

  private sealed class ArraySnapshot {
    public required long Generation { get; init; }
    public required PackageSummary[] Packages { get; init; }
  }

  private sealed class BrowseCache {
    public required long Generation { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required List<Match> Matches { get; init; }
  }

  private readonly record struct Match(
    PackageSummary Package,
    SummaryVersion Latest,
    SearchDetails Details,
    double Score,
    long Downloads
  );
}