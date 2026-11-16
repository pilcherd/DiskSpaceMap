using System.IO;

// Test-only hook: run the existing checks inside the published executable's own
// runtime, then exit before the production entry point can open a window.
public static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            string expectedRuntime = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")
                ?? throw new InvalidOperationException("Set an isolated bundle extraction directory for this check.");
            // Use the host's native probing directories: a single-file host need not
            // expose bundled libraries as individually named process modules.
            string[] nativeDirectories = ((string?)AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            string prefix = Path.GetFullPath(expectedRuntime) + Path.DirectorySeparatorChar;
            if (nativeDirectories.Length == 0 || nativeDirectories.Any(path => !Path.GetFullPath(path).StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Native runtime probing includes a location outside the bundled runtime.");
            // Depending on extraction mode, bundled managed assemblies either have
            // no Location or live in this bundle's isolated extraction directory.
            string coreLibrary = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(coreLibrary) && !Path.GetFullPath(coreLibrary).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The core runtime assembly was loaded from outside the executable.");
            using (var license = typeof(DiskSpaceMap.App).Assembly.GetManifestResourceStream("DiskSpaceMap.LICENSE"))
                if (license == null) throw new InvalidOperationException("The packaged executable is missing its project license.");
            Console.WriteLine("PASS  Core runtime assembly is bundled; native probing is isolated to: " + string.Join(", ", nativeDirectories));
            // Startup hooks can run before the entry point's STA attribute is applied.
            var thread = new Thread(() => DiskSpaceMap.RenderChecks.Program.Main([]));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        Environment.Exit(Environment.ExitCode);
    }
}
