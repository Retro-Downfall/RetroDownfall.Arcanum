using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed record AiReflectionAssembly(string ProjectPath, string AssemblyPath);

internal static class AiReflectionProductionClosure
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static IReadOnlyList<AiReflectionAssembly> ReadManifest(string path)
    {
        string[] lines = ReadFileLines(path);

        if (lines.Length == 0)
        {
            throw new InvalidDataException($"The AI reflection assembly manifest is empty: {path}");
        }

        List<AiReflectionAssembly> assemblies = [];

        foreach (string line in lines)
        {
            string[] fields = line.Split('|');

            if (fields.Length != 2
                || string.IsNullOrWhiteSpace(fields[0])
                || string.IsNullOrWhiteSpace(fields[1])
                || !Path.IsPathFullyQualified(fields[0])
                || !Path.IsPathFullyQualified(fields[1]))
            {
                throw new InvalidDataException($"The AI reflection assembly manifest requires absolute project|assembly paths: {path}");
            }

            assemblies.Add(new AiReflectionAssembly(fields[0], fields[1]));
        }

        return assemblies;
    }

    internal static IReadOnlyList<AiReflectionViolation> Scan(
        string repoRoot,
        IReadOnlyList<AiReflectionAssembly> assemblies)
    {
        string root = PhysicalPath(repoRoot);

        string sourceRoot = Path.Combine(root, "src");

        string nativeProject = PhysicalPath(Path.Combine(sourceRoot,
            "RetroDownfall.Arcanum.NativeSqlCipher", "RetroDownfall.Arcanum.NativeSqlCipher.csproj"));

        string[] projects = [.. AuthoredFiles(sourceRoot)
            .Where(static path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(PhysicalPath)
            .Order(StringComparer.Ordinal)];

        if (!projects.Contains(nativeProject, PathComparer))
        {
            throw new InvalidDataException("The reviewed SQLCipher asset project is missing from the production source inventory.");
        }

        VerifyNativeAssetProject(nativeProject);

        HashSet<string> expected = new(projects.Where(project => !PathComparer.Equals(project, nativeProject)), PathComparer);

        HashSet<string> observedProjects = new(PathComparer);

        HashSet<string> observedAssemblies = new(PathComparer);

        HashSet<string> observedIdentities = new(StringComparer.Ordinal);

        List<byte[]> images = [];

        foreach (AiReflectionAssembly assembly in assemblies.OrderBy(static assembly => assembly.ProjectPath, StringComparer.Ordinal))
        {
            string project = PhysicalPath(assembly.ProjectPath);

            string output = PhysicalPath(assembly.AssemblyPath);

            if (!expected.Contains(project)
                || !observedProjects.Add(project)
                || !observedAssemblies.Add(output))
            {
                throw new InvalidDataException($"Unexpected or duplicate production project/output in the AI reflection closure: {assembly}");
            }

            byte[] image;

            try
            {
                image = File.ReadAllBytes(output);

                using PEReader pe = new(new MemoryStream(image, writable: false));

                if (!pe.HasMetadata)
                {
                    throw new InvalidDataException($"The production output has no managed metadata: {output}");
                }

                MetadataReader metadata = pe.GetMetadataReader();

                if (!metadata.IsAssembly)
                {
                    throw new InvalidDataException($"The production output is not a managed assembly: {output}");
                }

                string identity = metadata.GetString(metadata.GetAssemblyDefinition().Name);

                if (!string.Equals(identity, ProjectAssemblyName(project), StringComparison.Ordinal)
                    || !observedIdentities.Add(identity))
                {
                    throw new InvalidDataException($"The production project/output assembly identity differs or is duplicated: {project} -> {identity}");
                }
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Cannot read the managed production output: {output}", exception);
            }

            images.Add(image);
        }

        if (!expected.SetEquals(observedProjects))
        {
            throw new InvalidDataException("Production projects missing from the AI reflection compiled closure: "
                + string.Join(", ", expected.Except(observedProjects, PathComparer).Order(StringComparer.Ordinal)));
        }

        return [.. images.SelectMany(AiReflectionBoundaryScanner.FindForbiddenReferences)
            .OrderBy(static violation => violation.Assembly, StringComparer.Ordinal)
            .ThenBy(static violation => violation.Caller, StringComparer.Ordinal)
            .ThenBy(static violation => violation.Offset)
            .ThenBy(static violation => violation.Callee, StringComparer.Ordinal)];
    }

    private static string ProjectAssemblyName(string project)
    {
        string[] names = [.. XDocument.Load(project).Descendants()
            .Where(static element => element.Name.LocalName == "AssemblyName")
            .Select(static element => element.Value)];

        if (names.Length == 0)
        {
            return Path.GetFileNameWithoutExtension(project);
        }

        if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0])
            || names[0].Contains('$')
            || names[0].Contains('@'))
        {
            throw new InvalidDataException($"The production assembly identity requires an explicit reviewed value: {project}");
        }

        return names[0];
    }

    private static void VerifyNativeAssetProject(string project)
    {
        XDocument document = XDocument.Load(project);

        bool ExplicitlyFalse(string name) => document.Descendants()
            .Where(element => element.Name.LocalName == name)
            .Select(static element => element.Value.Trim())
            .SequenceEqual(["false"], StringComparer.OrdinalIgnoreCase);

        if (!ExplicitlyFalse("EnableDefaultItems")
            || !ExplicitlyFalse("IncludeBuildOutput")
            || document.Descendants().Any(static element => element.Name.LocalName == "Compile"
                || element.Name.LocalName == "EnableDefaultCompileItems" && !string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            || AuthoredFiles(Path.GetDirectoryName(project)!).Any(static path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"The SQLCipher exclusion requires an asset-only project without managed compilation inputs: {project}");
        }
    }

    private static IEnumerable<string> AuthoredFiles(string directory)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            if (entry is DirectoryInfo && (entry.Name is "bin" or "obj"))
            {
                continue;
            }

            if (entry.LinkTarget is not null)
            {
                throw new InvalidDataException($"A symbolic link cannot hide production inventory inputs: {entry.FullName}");
            }

            if (entry is DirectoryInfo)
            {
                foreach (string file in AuthoredFiles(entry.FullName))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry.FullName;
            }
        }
    }

    private static string PhysicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"The production closure requires absolute paths: {path}");
        }

        string fullPath = Path.GetFullPath(path);

        string current = Path.GetPathRoot(fullPath)!;

        foreach (string component in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);

            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);

            if (entry.LinkTarget is not null)
            {
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new InvalidDataException($"Cannot resolve a production closure symbolic link: {current}");
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string[] ReadFileLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read the AI reflection assembly manifest: {path}", exception);
        }
    }
}
