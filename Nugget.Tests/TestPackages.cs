using System.IO.Compression;
using System.Security;
using System.Text;

namespace Nugget.Tests;

public sealed class TestPackage {
  public required string Id { get; init; }
  public required string Version { get; init; }
  public string Description { get; init; } = "A test package.";
  public string Authors { get; init; } = "Test Author";
  public string? Title { get; init; }
  public string? Tags { get; init; }
  public string? Summary { get; init; }
  public string? LicenseExpression { get; init; }
  public string? LicenseFile { get; init; }
  public string? Readme { get; init; }
  public byte[]? Icon { get; init; }
  public string? PackageType { get; init; }

  /// <summary>(target framework, dependency ID, version range)</summary>
  public List<(string? Framework, string Id, string Range)> Dependencies {
    get;
    init;
  } =
    [];

  /// <summary>Changes the package bytes without changing its metadata.</summary>
  public string Salt { get; init; } = "";
}

/// <summary>Builds small but valid <c>.nupkg</c> files.</summary>
public static class TestPackages {
  public static readonly byte[] S_PNG = [
    0x89,
    0x50,
    0x4E,
    0x47,
    0x0D,
    0x0A,
    0x1A,
    0x0A,
    0x00
  ];

  public static byte[] Create(string id, string version) {
    return Create(
      new TestPackage {
        Id = id,
        Version = version
      }
    );
  }

  public static byte[] Create(TestPackage package) {
    using MemoryStream buffer = new();
    using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, true)) {
      Add(zip, $"{package.Id}.nuspec", Nuspec(package));
      Add(
        zip,
        "[Content_Types].xml",
        """
        <?xml version="1.0" encoding="utf-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="nuspec" ContentType="application/octet" />
        </Types>
        """
      );
      Add(zip, "lib/netstandard2.0/_._", package.Salt);
      if (package.Readme != null) {
        Add(zip, "README.md", package.Readme);
      }

      if (package.LicenseFile != null) {
        Add(zip, "LICENSE.txt", package.LicenseFile);
      }

      if (package.Icon != null) {
        ZipArchiveEntry entry = zip.CreateEntry("images/icon.png");
        using Stream stream = entry.Open();
        stream.Write(package.Icon);
      }
    }

    return buffer.ToArray();
  }

  private static string Nuspec(TestPackage package) {
    StringBuilder xml = new();
    xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
    xml.Append(
      "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n"
    );
    xml.Append("  <metadata>\n");
    Element(xml, "id", package.Id);
    Element(xml, "version", package.Version);
    Element(xml, "authors", package.Authors);
    Element(xml, "description", package.Description);
    Element(xml, "title", package.Title);
    Element(xml, "tags", package.Tags);
    Element(xml, "summary", package.Summary);
    if (package.LicenseExpression != null) {
      xml.Append(
        $"    <license type=\"expression\">{Escape(package.LicenseExpression)}</license>\n"
      );
    } else if (package.LicenseFile != null) {
      xml.Append("    <license type=\"file\">LICENSE.txt</license>\n");
    }

    if (package.Readme != null) {
      Element(xml, "readme", "README.md");
    }

    if (package.Icon != null) {
      Element(xml, "icon", "images\\icon.png");
    }

    if (package.PackageType != null) {
      xml.Append(
        $"    <packageTypes><packageType name=\"{Escape(package.PackageType)}\" /></packageTypes>\n"
      );
    }

    if (package.Dependencies.Count > 0) {
      xml.Append("    <dependencies>\n");
      foreach (IGrouping<string?, (string? Framework, string Id, string Range)>
                 group in package.Dependencies.GroupBy(d => d.Framework)) {
        xml.Append(
          group.Key == null
            ? "      <group>\n"
            : $"      <group targetFramework=\"{group.Key}\">\n"
        );
        foreach ((string? _, string id, string range) in group) {
          xml.Append(
            $"        <dependency id=\"{Escape(id)}\" version=\"{Escape(range)}\" />\n"
          );
        }

        xml.Append("      </group>\n");
      }

      xml.Append("    </dependencies>\n");
    }

    xml.Append("  </metadata>\n</package>\n");
    return xml.ToString();
  }

  private static void Element(StringBuilder xml, string name, string? value) {
    if (value != null) {
      xml.Append($"    <{name}>{Escape(value)}</{name}>\n");
    }
  }

  private static string Escape(string value) {
    return SecurityElement.Escape(value);
  }

  private static void Add(ZipArchive zip, string name, string content) {
    ZipArchiveEntry entry = zip.CreateEntry(name);
    using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
    writer.Write(content);
  }
}