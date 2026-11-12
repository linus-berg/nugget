using System.IO.Compression;
using System.Xml;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Nugget.Api.Packages;

/// <summary>An uploaded package that passed validation.</summary>
public sealed class InspectedPackage {
  public required PackageVersionRecord Record { get; init; }
  public required byte[] Nuspec { get; init; }
  public byte[]? Icon { get; init; }
  public byte[]? Readme { get; init; }
  public byte[]? License { get; init; }
}

/// <summary>The upload is not a usable NuGet package.</summary>
public sealed class InvalidPackageException(string message)
  : Exception(message);

/// <summary>Reads the manifest and embedded files of a <c>.nupkg</c>.</summary>
public static class PackageInspector {
  private const int C_MAX_NUSPEC_BYTES_ = 8 * 1024 * 1024;

  // nuget.org limits icons, readmes and licenses to 1 MB; anything much
  // larger is not served (the package itself is still accepted).
  private const int C_MAX_ASSET_BYTES_ = 4 * 1024 * 1024;

  public static InspectedPackage Inspect(Stream package, long size,
                                         string sha512) {
    try {
      using PackageArchiveReader reader = new(package, true);
      return Inspect(reader, size, sha512);
    } catch (InvalidPackageException) {
      throw;
    } catch (Exception ex) when (ex is InvalidDataException
                                   or XmlException
                                   or PackagingException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or FormatException
                                   or NullReferenceException) {
      throw new InvalidPackageException(
        $"Not a valid NuGet package: {ex.Message}"
      );
    }
  }

  private static InspectedPackage Inspect(PackageArchiveReader reader,
                                          long size, string sha512) {
    byte[] nuspec_bytes;
    using (Stream nuspec_stream = reader.GetNuspec()) {
      nuspec_bytes = ReadLimited(nuspec_stream, C_MAX_NUSPEC_BYTES_) ??
                     throw new InvalidPackageException(
                       "The nuspec is too large."
                     );
    }

    NuspecReader nuspec = new(new MemoryStream(nuspec_bytes));
    string id = nuspec.GetId();
    if (!PackageIdValidator.IsValidPackageId(id) ||
        id.Length > PackageIdValidator.MaxPackageIdLength) {
      throw new InvalidPackageException($"Invalid package ID '{id}'.");
    }

    NuGetVersion version = nuspec.GetVersion();
    if (nuspec.GetPackageTypes()
              .Any(
                t => t.Name.Equals(
                  PackageType.SymbolsPackage.Name,
                  StringComparison.OrdinalIgnoreCase
                )
              )) {
      throw new InvalidPackageException(
        "Symbol packages (.snupkg) are not supported."
      );
    }

    List<DependencyGroupRecord> groups = nuspec
                                         .GetDependencyGroups(false)
                                         .Select(ToRecord)
                                         .ToList();
    LicenseMetadata? license = nuspec.GetLicenseMetadata();
    string? icon_path = nuspec.GetIcon();
    string? readme_path = nuspec.GetReadme();
    string? license_path = license?.Type == LicenseType.File
                             ? license.License
                             : null;
    byte[]? icon = ReadEntry(reader, icon_path);
    byte[]? readme = ReadEntry(reader, readme_path);
    byte[]? license_file = ReadEntry(reader, license_path);
    RepositoryMetadata repository = nuspec.GetRepositoryMetadata();

    PackageVersionRecord record = new() {
      Id = id,
      Version = version.ToFullString(),
      OriginalVersion = version.OriginalVersion,
      IsSemVer2 = version.IsSemVer2 || groups.Any(HasSemVer2Range),
      Published = DateTimeOffset.UtcNow,
      Authors = nuspec.GetAuthors(),
      Description = nuspec.GetDescription(),
      Summary = Empty(nuspec.GetSummary()),
      Title = Empty(nuspec.GetTitle()),
      Tags = Empty(nuspec.GetTags()),
      Copyright = Empty(nuspec.GetCopyright()),
      ReleaseNotes = Empty(nuspec.GetReleaseNotes()),
      Language = Empty(nuspec.GetLanguage()),
      ProjectUrl = Empty(nuspec.GetProjectUrl()),
      IconUrl = Empty(nuspec.GetIconUrl()),
      LicenseUrl = Empty(nuspec.GetLicenseUrl()),
      LicenseExpression = license?.Type == LicenseType.Expression
                            ? license.License
                            : null,
      MinClientVersion = nuspec.GetMinClientVersion()?.ToNormalizedString(),
      RequireLicenseAcceptance = nuspec.GetRequireLicenseAcceptance(),
      DevelopmentDependency = nuspec.GetDevelopmentDependency(),
      IconContentType = icon == null ? null : DetectImageType(icon),
      HasReadme = readme != null,
      HasLicenseFile = license_file != null,
      Repository = string.IsNullOrEmpty(repository.Url)
                     ? null
                     : new RepositoryRecord {
                       Type = Empty(repository.Type),
                       Url = repository.Url,
                       Branch = Empty(repository.Branch),
                       Commit = Empty(repository.Commit)
                     },
      DependencyGroups = groups,
      PackageTypes = nuspec.GetPackageTypes()
                           .Select(
                             t => new PackageTypeRecord {
                               Name = t.Name,
                               Version = t.Version == PackageType.EmptyVersion
                                           ? null
                                           : t.Version.ToString()
                             }
                           )
                           .ToList(),
      PackageSize = size,
      PackageHash = sha512
    };
    return new InspectedPackage {
      Record = record,
      Nuspec = nuspec_bytes,
      Icon = icon,
      Readme = readme,
      License = license_file
    };
  }

  private static DependencyGroupRecord ToRecord(PackageDependencyGroup group) {
    NuGetFramework framework = group.TargetFramework;
    return new DependencyGroupRecord {
      TargetFramework = framework.IsAny ? null : framework.GetShortFolderName(),
      Dependencies = group.Packages
                          .Select(
                            d => new DependencyRecord {
                              Id = d.Id,
                              Range = d.VersionRange.ToNormalizedString()
                            }
                          )
                          .ToList()
    };
  }

  private static bool HasSemVer2Range(DependencyGroupRecord group) {
    // nuget.org treats a package as SemVer 2.0.0 when any dependency range
    // mentions a SemVer 2.0.0 version, hiding it from older clients.
    return group.Dependencies.Any(
      d => VersionRange.TryParse(d.Range, out VersionRange? range) &&
           (range.MinVersion?.IsSemVer2 == true ||
            range.MaxVersion?.IsSemVer2 == true)
    );
  }

  private static byte[]? ReadEntry(PackageArchiveReader reader, string? path) {
    if (string.IsNullOrWhiteSpace(path)) {
      return null;
    }

    string normalized = path.Replace('\\', '/').TrimStart('/');
    string? file = reader.GetFiles()
                         .FirstOrDefault(
                           f => string.Equals(
                             f.Replace('\\', '/').TrimStart('/'),
                             normalized,
                             StringComparison.OrdinalIgnoreCase
                           )
                         );
    if (file == null) {
      return null;
    }

    ZipArchiveEntry entry = reader.GetEntry(file);
    if (entry.Length > C_MAX_ASSET_BYTES_) {
      return null;
    }

    using Stream stream = entry.Open();
    return ReadLimited(stream, C_MAX_ASSET_BYTES_);
  }

  private static byte[]? ReadLimited(Stream stream, int max_bytes) {
    using MemoryStream buffer = new();
    byte[] chunk = new byte[81920];
    int read;
    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0) {
      if (buffer.Length + read > max_bytes) {
        return null;
      }

      buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
  }

  /// <summary>
  ///   Icons are served from this host, so only well known raster formats
  ///   get an image content type (never SVG, which can carry scripts).
  /// </summary>
  public static string DetectImageType(ReadOnlySpan<byte> data) {
    if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])) {
      return "image/png";
    }

    if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) {
      return "image/jpeg";
    }

    if (data.StartsWith("GIF8"u8)) {
      return "image/gif";
    }

    if (data.Length > 12 &&
        data.StartsWith("RIFF"u8) &&
        data[8..12].SequenceEqual("WEBP"u8)) {
      return "image/webp";
    }

    if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00])) {
      return "image/x-icon";
    }

    return "application/octet-stream";
  }

  private static string? Empty(string? value) {
    return string.IsNullOrWhiteSpace(value) ? null : value;
  }
}