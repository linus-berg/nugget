using System.Text;

namespace Nugget.Api.Catalog;

public enum SearchField {
  ANY,
  ID,
  PACKAGE_ID,
  VERSION,
  TITLE,
  DESCRIPTION,
  SUMMARY,
  TAGS,
  AUTHORS
}

public sealed class SearchTerm {
  public required SearchField Field { get; init; }
  public required string Value { get; init; }
}

/// <summary>
///   A parsed search query in nuget.org syntax: free words, quoted phrases
///   and <c>field:value</c> filters (<c>id:</c>, <c>packageid:</c>,
///   <c>version:</c>, <c>title:</c>, <c>description:</c>, <c>summary:</c>,
///   <c>tags:</c>, <c>authors:</c>, <c>owners:</c>).
/// </summary>
public sealed class SearchQuery {
  private SearchQuery(string text, List<SearchTerm> terms) {
    Text = text;
    Terms = terms;
  }

  public string Text { get; }
  public IReadOnlyList<SearchTerm> Terms { get; }
  public bool IsEmpty => Terms.Count == 0;

  public static SearchQuery Parse(string? query) {
    string text = (query ?? "").Trim();
    List<SearchTerm> terms = [];
    int position = 0;
    while (position < text.Length) {
      if (char.IsWhiteSpace(text[position])) {
        position++;
        continue;
      }

      SearchField field = SearchField.ANY;
      int colon = FindFieldSeparator(text, position);
      if (colon > position) {
        SearchField? parsed = ParseField(text[position..colon]);
        if (parsed != null) {
          field = parsed.Value;
          position = colon + 1;
        }
      }

      string value = ReadValue(text, ref position);
      if (value.Length > 0) {
        terms.Add(
          new SearchTerm {
            Field = field,
            Value = value
          }
        );
      }
    }

    return new SearchQuery(text, terms);
  }

  private static int FindFieldSeparator(string text, int start) {
    for (int i = start; i < text.Length; i++) {
      char c = text[i];
      if (c == ':') {
        return i;
      }

      if (!char.IsLetter(c)) {
        return -1;
      }
    }

    return -1;
  }

  private static SearchField? ParseField(string name) {
    return name.ToLowerInvariant() switch {
      "id" => SearchField.ID,
      "packageid" => SearchField.PACKAGE_ID,
      "version" => SearchField.VERSION,
      "title" => SearchField.TITLE,
      "description" => SearchField.DESCRIPTION,
      "summary" => SearchField.SUMMARY,
      "tag" or "tags" => SearchField.TAGS,
      // Owners are not mirrored; the authors are the closest match.
      "author" or "authors" or "owner" or "owners" => SearchField.AUTHORS,
      _ => null
    };
  }

  private static string ReadValue(string text, ref int position) {
    StringBuilder value = new();
    if (position < text.Length && text[position] == '"') {
      position++;
      while (position < text.Length && text[position] != '"') {
        value.Append(text[position++]);
      }

      position++;
      return value.ToString().Trim();
    }

    while (position < text.Length && !char.IsWhiteSpace(text[position])) {
      value.Append(text[position++]);
    }

    return value.ToString().Trim('"');
  }
}