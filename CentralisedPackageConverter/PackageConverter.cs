using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace CentralisedPackageConverter;

public class PackageConverter
{
    private IDictionary<string, string> allReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private const string s_DirPackageProps = "Directory.Packages.props";

    private static readonly HashSet<string> s_extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".vbproj",
        ".props",
        ".targets"
    };

    public void ProcessConversion(string solutionFolder, bool revert, bool dryRun, bool force)
    {
        var packageConfigPath = Path.Combine(solutionFolder, s_DirPackageProps);

        if (dryRun)
            Console.WriteLine("Dry run enabled - no changes will be made on disk.");

        var rootDir = new DirectoryInfo(solutionFolder);

        // Find all the csproj files to process
        var projects = rootDir.GetFiles("*.*", SearchOption.AllDirectories)
                              .Where(x => s_extensions.Contains(x.Extension))
                              .Where(x => !x.Name.Equals(s_DirPackageProps))
                              .OrderBy(x => x.Name)
                              .ToList();

        if (!force && !dryRun)
        {
            Console.WriteLine("WARNING: You are about to make changes to the following project files:");
            projects.ForEach(p => Console.WriteLine($" {p.Name}"));
            Console.WriteLine("Are you sure you want to continue? [y/n]");
            if (Console.ReadKey().Key != ConsoleKey.Y)
            {
                Console.WriteLine("Aborting...");
                return;
            }
        }

        // If we're reverting, read the references from the central file.
        if (revert)
            ReadDirectoryPackagePropsFile(packageConfigPath);

        if (revert)
        {
            projects.ForEach(proj => RevertProject(proj, dryRun));

            if (!dryRun)
            {
                Console.WriteLine($"Deleting {packageConfigPath}...");
                File.Delete(packageConfigPath);
            }
        }
        else
        {
            ConvertFromPaketDependenciesFile(rootDir, dryRun);

            foreach (var proj in projects.Where(p => File.Exists(p.FullName)))
            {
                ConvertProject(proj, dryRun);
                ConvertFromPaketReferenceFiles(proj, dryRun);
            }

            if (allReferences.Any())
            {
                WriteDirectoryPackagesConfig(packageConfigPath, dryRun);
            }
            else
                Console.WriteLine("No versioned references found in csproj files!");
        }
    }

    /// <summary>
    /// Revert a project to non-centralised package management
    /// by adding the versions back into the csproj file.
    /// </summary>
    /// <param name="project"></param>
    /// <param name="dryRun"></param>
    private void RevertProject(FileInfo project, bool dryRun)
    {
        var xml = XDocument.Load(project.FullName);

        var refs = xml.Descendants("PackageReference");

        bool needToWriteChanges = false;

        foreach (var reference in refs)
        {
            var package = GetAttributeValue(reference, "Include", false);

            if (allReferences.TryGetValue(package, out var version))
            {
                reference.SetAttributeValue("Version", version);
                needToWriteChanges = true;
            }
            else
                Console.WriteLine($"No version found in {s_DirPackageProps} file for {package}! Skipping...");
        }

        if (!dryRun && needToWriteChanges)
            xml.Save(project.FullName);
    }

    /// <summary>
    /// Read the list of references and versions from the Directory.Package.props file.
    /// </summary>
    /// <param name="packageConfigPath"></param>
    private void ReadDirectoryPackagePropsFile(string packageConfigPath)
    {
        var xml = XDocument.Load(packageConfigPath);

        var refs = xml.Descendants("PackageVersion");

        foreach (var reference in refs)
        {
            var package = GetAttributeValue(reference, "Include", false);
            var version = GetAttributeValue(reference, "Version", false);

            allReferences[package] = version;
        }

        Console.WriteLine($"Read {allReferences.Count} references from {packageConfigPath}");
    }

    /// <summary>
    /// Write the packages.config file.
    /// TODO: Would be good to read the existing file and merge if appropriate.
    /// </summary>
    /// <param name="solutionFolder"></param>
    private void WriteDirectoryPackagesConfig(string packageConfigPath, bool dryRun)
    {
        var lines = new List<string>();

        lines.Add("<Project>");
        lines.Add("  <PropertyGroup>");
        lines.Add("    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        lines.Add("  </PropertyGroup>");
        lines.Add("  <ItemGroup>");

        // Implicit references will cause build error NU1009 with nuget CPM, so skip them
        var implicitReferences = new string[] { "NETStandard.Library" }.ToHashSet();

        foreach (var kvp in allReferences.Where(r => !implicitReferences.Contains(r.Key)).OrderBy(x => x.Key))
        {
            lines.Add($"    <PackageVersion Include=\"{kvp.Key}\" Version=\"{kvp.Value}\" />");
        }

        lines.Add("  </ItemGroup>");
        lines.Add("</Project>");

        Console.WriteLine($"Writing {allReferences.Count} refs to {s_DirPackageProps} to {packageConfigPath}...");

        if (dryRun)
            lines.ForEach(x => Console.WriteLine(x));
        else
            File.WriteAllLines(packageConfigPath, lines);
    }

    /// <summary>
    /// Safely get an attribute value from an XML Element, optionally
    /// deleting it after the value has been retrieved.
    /// </summary>
    /// <param name="elem"></param>
    /// <param name="name"></param>
    /// <param name="remove"></param>
    /// <returns></returns>
    private string? GetAttributeValue(XElement elem, string name, bool remove)
    {
        var attr = elem.Attributes(name);

        if (attr != null)
        {
            var value = attr.Select(x => x.Value).FirstOrDefault();
            if (remove)
                attr.Remove();
            return value;
        }

        return null;
    }

    /// <summary>
    /// Converts a csproj to Centrally managed packaging.
    /// </summary>
    /// <param name="csprojFile"></param>
    /// <param name="dryRun"></param>
	private void ConvertProject(FileInfo csprojFile, bool dryRun)
    {
        Console.WriteLine($"Processing references for {csprojFile.FullName}...");

        var xml = XDocument.Load(csprojFile.FullName, LoadOptions.PreserveWhitespace);

        var refs = xml.Descendants("PackageReference");

        bool needToWriteChanges = false;

        var referencesToRemove = new List<XElement>();
        foreach (var reference in refs)
        {
            var removeNodeIfEmpty = false;

            var package = GetAttributeValue(reference, "Include", false);

            if (string.IsNullOrEmpty(package))
            {
                package = GetAttributeValue(reference, "Update", false);
                removeNodeIfEmpty = true;
            }

            if (string.IsNullOrEmpty(package))
                continue;

            var version = GetAttributeValue(reference, "Version", true);

            if (!string.IsNullOrEmpty(version))
            {
                // If there is only an Update attribute left, and no child elements, then this node
                // isn't useful any more, so we can remove it entirely
                if (removeNodeIfEmpty && reference.Attributes().Count() == 1 && !reference.Elements().Any())
                    referencesToRemove.Add(reference);

                needToWriteChanges = true;

                if (allReferences.TryGetValue(package, out var existingVer))
                {
                    // Existing reference for this package of same or greater version, so skip
                    if (version.CompareTo(existingVer) >= 0)
                        continue;
                }

                Console.WriteLine($" Found new reference: {package} {version}");
                allReferences[package] = version;
            }
        }

        foreach (var reference in referencesToRemove)
        {
            reference.Remove();
        }

        if (needToWriteChanges && !dryRun)
            xml.Save(csprojFile.FullName);
    }

    private void ConvertFromPaketDependenciesFile(DirectoryInfo directory, bool dryRun)
    {
        var packagesDirectory = Path.Combine(directory.FullName, "packages");
        var paketDirectory = Path.Combine(directory.FullName, ".paket");
        var paketFilesDirectory = Path.Combine(directory.FullName, "paket-files");
        var paketDependenciesFile = Path.Combine(directory.FullName, "paket.dependencies");
        var paketLockFile = Path.Combine(directory.FullName, "paket.lock");

        var dependenciesVersions = File // These are the package names we want to transfer to Directory.Build.props
            .ReadAllLines(paketDependenciesFile)
            .Where(l => l.StartsWith("nuget"))
            .Select(l => l.Split()[1]); // l[0] nuget l[1] Name l[2 - ∞] metadata
        var lockVersions = File // This is where we find the actual version numbers
            .ReadAllLines(paketLockFile)
            .Where(l => l.StartsWith("    ")) // Lines starting with 4 space indents are the base packages
            .Select(l => l.Remove(0, 4))
            .Where(l => !l.StartsWith(" ")) // If there are still lines starting with spaces they are transitive packages, dump them
            .Select(l => l.Split(" "))
            .ToDictionary(l => l[0], l => l[1].Trim('(', ')'), StringComparer.OrdinalIgnoreCase); // l[0] Name l[1] Version l[2 - ∞] metadata

        foreach (var d in dependenciesVersions)
            allReferences.TryAdd(d, lockVersions[d]);
        foreach (var d in lockVersions)
            allReferences.TryAdd(d.Key, d.Value);

        if (!dryRun)
        {
            if (Directory.Exists(packagesDirectory))
                Directory.Delete(packagesDirectory, true);
            else
                Console.WriteLine("Packages directory not found, you must delete it manually.");
            if (Directory.Exists(paketDirectory))
                Directory.Delete(paketDirectory, true);
            if (Directory.Exists(paketFilesDirectory))
                Directory.Delete(paketFilesDirectory, true);
            if (File.Exists(paketDependenciesFile))
                File.Delete(paketDependenciesFile);
            if (File.Exists(paketLockFile))
                File.Delete(paketLockFile);
        }
    }

    private void ConvertFromPaketReferenceFiles(FileInfo csprojFile, bool dryRun)
    {
        var paketFile = Path.Combine(csprojFile.Directory.FullName, "paket.references");

        if (File.Exists(paketFile))
        {
            var xml = XDocument.Load(csprojFile.FullName);
            //var xml = XDocument.Load(csprojFile.FullName, LoadOptions.PreserveWhitespace);
            var @namespace = xml.Descendants().First(d => d.Name.LocalName == "Project").Name.Namespace;
            var paketReferences = File.ReadLines(paketFile);

            if (paketReferences.Any())
            {
                // Find or create ItemGroup with PackageReferences
                var packageReferences = xml
                    .Descendants()
                    .Where(d => d.Name.LocalName == "PackageReference")
                    .FirstOrDefault()
                    ?.Parent;

                if (packageReferences == null)
                {
                    packageReferences = new XElement(@namespace + "ItemGroup");
                    // Now we need to put the ItemGroup somewhere smart
                    var targetPosition = xml
                        .Descendants()
                        .Where(d => d.Name.LocalName == "ProjectReference")
                        .FirstOrDefault()
                        ?.Parent; // VS puts it here

                    if (targetPosition != null)
                    {
                        targetPosition.AddBeforeSelf(packageReferences);
                    }
                    else
                    {
                        targetPosition =
                            xml
                                .Descendants()
                                .Where(d => d.Name.LocalName == "PropertyGroup")
                                .LastOrDefault() ?? // Put our packages after the last ItemGroup
                            xml.Descendants().Last(); // Dump it at the end of the file if everything else fails
                        targetPosition.AddAfterSelf(packageReferences);
                    }
                }

                // Add PackageReference items from paket
                foreach (var reference in paketReferences.Where(r => !string.IsNullOrEmpty(r)))
                {
                    var line = reference.Split("#"); // line[0] reference line[1] comment

                    if (line.Length > 1 && !string.IsNullOrEmpty(line[1]))
                        packageReferences.Add(new XComment(line[1]));
                    if (!string.IsNullOrEmpty(line[0]))
                    {
                        var package = line[0].Split(" "); // package[0] package name package[1 - ∞] metadata
                        packageReferences.Add(new XElement(@namespace + "PackageReference", new XAttribute("Include", package[0])));
                    }

                    //allReferences.TryAdd("", "");
                }
            }

            // Remove Import paket from csproj
            foreach (var e in xml.Descendants("Import").Where(e => e.Attribute("Project").Value.Contains(".paket")).ToArray())
            {
                if (!dryRun)
                    e.Remove();
            }

            // Remove paket generated bindings from web/app config
            var configs = Directory
                .GetFiles(csprojFile.Directory.FullName)
                .Where(f => f.EndsWith(".config"))
                .Where(f => f.Contains("app.", StringComparison.OrdinalIgnoreCase) || f.Contains("web.", StringComparison.OrdinalIgnoreCase));
            foreach (var config in configs)
            {
                var xmlConfig = XDocument.Load(config);

                var test = xmlConfig.Descendants().Reverse().Take(10).ToArray();
                var dependentAssemblies = xmlConfig
                    .Descendants("{urn:schemas-microsoft-com:asm.v1}dependentAssembly")
                    // Only get nodes generated by Paket
                    .Where(da => da.Descendants("{urn:schemas-microsoft-com:asm.v1}Paket").Select(p => p.Value).All(v => v == "True"))
                    .ToArray();

                var dependentAssembliesParent = dependentAssemblies.FirstOrDefault()?.Parent;
                dependentAssemblies.Remove();
                if (dependentAssembliesParent?.HasElements == false)
                {
                    dependentAssembliesParent?.Parent.Remove();
                }

                xmlConfig.Save(config);
            }

            if (dryRun)
            {
                Console.WriteLine("Deleting " + paketFile);
            }
            else
            {
                xml.Save(csprojFile.FullName);
                File.Delete(paketFile);
            }
        }
    }
}
