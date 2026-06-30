using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.IO;
using System.Text.Json;
using ArisenKernel.Services;
using ArisenKernel.Packages;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;

using System.Runtime.InteropServices;

namespace ArisenEngine.Testing;

public class TestRunnerService : ITestRunner
{
    private readonly Dictionary<string, Action> _nativeTests = new();
    private readonly HashSet<string> _discoveredNativeTestEntries = new(StringComparer.OrdinalIgnoreCase);

    private delegate void RegisterCallback(string name, IntPtr actionPtr);
    private delegate void NativeRegisterFunc(RegisterCallback callback);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

    public bool RunAll()
    {
        // Discover Native Tests first
        DiscoverNativeTests();

        KernelLog.Info("[TestRunner] Starting Global Test Run...");
        int passed = 0;
        int failed = 0;

        // 1. Managed Test Discovery
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            // Skip system assemblies to speed up discovery
            string assemblyName = assembly.FullName ?? string.Empty;
            if (assemblyName.StartsWith("System", StringComparison.Ordinal) || assemblyName.StartsWith("Microsoft", StringComparison.Ordinal))
                continue;

            var methods = assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttributes(typeof(ArisenTestAttribute), false).Length > 0);

            foreach (var method in methods)
            {
                var attr = (ArisenTestAttribute)method.GetCustomAttribute(typeof(ArisenTestAttribute))!;
                string testName = $"{method.DeclaringType?.Name}.{method.Name}";
                
                KernelLog.Info($"[TestRunner] Running Managed Test: {testName} - {attr.Description}");

                try
                {
                    // For now, only support static parameterless methods for simplicity
                    if (method.IsStatic)
                    {
                        method.Invoke(null, null);
                        passed++;
                        KernelLog.Info($"[TestRunner] [PASS] {testName}");
                    }
                    else
                    {
                        KernelLog.Warning($"[TestRunner] [SKIP] {testName} (Instance methods not yet supported)");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    KernelLog.Error($"[TestRunner] [FAIL] {testName}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        // 2. Native Test Execution
        foreach (var nativeTest in _nativeTests)
        {
            KernelLog.Info($"[TestRunner] Running Native Test: {nativeTest.Key}");
            try
            {
                nativeTest.Value.Invoke();
                passed++;
                KernelLog.Info($"[TestRunner] [PASS] {nativeTest.Key}");
            }
            catch (Exception ex)
            {
                failed++;
                KernelLog.Error($"[TestRunner] [FAIL] {nativeTest.Key}: {ex.Message}");
            }
        }

        KernelLog.Info($"[TestRunner] Test Run Finished. Passed: {passed}, Failed: {failed}");
        return failed == 0;
    }

    public void RegisterNativeTest(string name, Action action)
    {
        _nativeTests[name] = action;
        KernelLog.Info($"[TestRunner] Registered Native Test: {name}");
    }

    private void DiscoverNativeTests()
    {
        string resolvedManifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.resolved.json");
        if (!File.Exists(resolvedManifestPath))
        {
            KernelLog.Info("[TestRunner] No resolved manifest found; skipping native test discovery.");
            return;
        }

        ResolvedManifest? manifest;
        try
        {
            string json = File.ReadAllText(resolvedManifestPath);
            manifest = JsonSerializer.Deserialize<ResolvedManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            RegisterNativeDiscoveryFailure("NativeDiscovery.Manifest", $"Failed to read resolved manifest '{resolvedManifestPath}': {ex.Message}");
            return;
        }

        if (manifest?.ResolvedPackages == null || manifest.ResolvedPackages.Count == 0)
        {
            KernelLog.Info("[TestRunner] Resolved manifest contains no packages; skipping native test discovery.");
            return;
        }

        int discovered = 0;
        foreach (var package in manifest.ResolvedPackages)
        {
            if (package.NativeTests == null) continue;

            foreach (var nativeTest in EnumerateNativeTests(package))
            {
                string discoveryKey = $"{package.Id}:{nativeTest.Library}:{nativeTest.RegisterExport}";
                if (!_discoveredNativeTestEntries.Add(discoveryKey))
                {
                    continue;
                }

                DiscoverNativeTestLibrary(package.Id, nativeTest.Library, nativeTest.RegisterExport);
                discovered++;
            }
        }

        KernelLog.Info($"[TestRunner] Native test discovery complete. Declarations: {discovered}, Registered tests: {_nativeTests.Count}");
    }

    private void DiscoverNativeTestLibrary(string packageId, string library, string registerExport)
    {
        IntPtr hModule = LoadLibrary(library);
        if (hModule == IntPtr.Zero)
        {
            RegisterNativeDiscoveryFailure(
                $"NativeDiscovery.{packageId}.{library}",
                $"Native test library '{library}' from package '{packageId}' could not be loaded. Win32 error: {Marshal.GetLastWin32Error()}");
            return;
        }

        IntPtr funcPtr = GetProcAddress(hModule, registerExport);
        if (funcPtr == IntPtr.Zero)
        {
            RegisterNativeDiscoveryFailure(
                $"NativeDiscovery.{packageId}.{library}.{registerExport}",
                $"Native test library '{library}' from package '{packageId}' does not export '{registerExport}'.");
            return;
        }

        var registerFunc = Marshal.GetDelegateForFunctionPointer<NativeRegisterFunc>(funcPtr);

        RegisterCallback callback = (name, actionPtr) =>
        {
            var action = Marshal.GetDelegateForFunctionPointer<Action>(actionPtr);
            RegisterNativeTest(name, action);
        };

        try
        {
            registerFunc(callback);
            KernelLog.Info($"[TestRunner] Loaded native test declaration: {packageId} -> {library}!{registerExport}");
        }
        catch (Exception ex)
        {
            RegisterNativeDiscoveryFailure(
                $"NativeDiscovery.{packageId}.{library}.{registerExport}",
                $"Native test registration failed for '{library}' from package '{packageId}': {ex.Message}");
        }
    }

    private void RegisterNativeDiscoveryFailure(string name, string message)
    {
        RegisterNativeTest(name, () => throw new InvalidOperationException(message));
        KernelLog.Warning($"[TestRunner] {message}");
    }

    private static IEnumerable<NativeTestDeclaration> EnumerateNativeTests(ResolvedPackage package)
    {
        foreach (var ridEntry in package.NativeTests ?? new Dictionary<string, List<JsonElement>>())
        {
            if (!string.Equals(ridEntry.Key, "win-x64", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var element in ridEntry.Value)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    string? library = element.GetString();
                    if (!string.IsNullOrWhiteSpace(library))
                    {
                        yield return new NativeTestDeclaration(library, "RegisterNativeTests");
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    string? library = ReadStringProperty(element, "library") ?? ReadStringProperty(element, "name");
                    string registerExport = ReadStringProperty(element, "registerExport")
                        ?? ReadStringProperty(element, "export")
                        ?? "RegisterNativeTests";

                    if (!string.IsNullOrWhiteSpace(library))
                    {
                        yield return new NativeTestDeclaration(library, registerExport);
                    }
                }
            }
        }
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private sealed class ResolvedManifest
    {
        public List<ResolvedPackage>? ResolvedPackages { get; set; }
    }

    private sealed class ResolvedPackage
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, List<JsonElement>>? NativeTests { get; set; }
    }

    private sealed record NativeTestDeclaration(string Library, string RegisterExport);
}

public class TestRunnerHost : IApplicationHost
{
    private readonly IServiceRegistry _services;

    public TestRunnerHost(IServiceRegistry services)
    {
        _services = services;
    }

    public void Run(string[] args)
    {
        KernelLog.Info("[TestRunnerHost] Identifying Testing Profile... Hand-off successful.");
        
        var runner = _services.GetService<ITestRunner>();
        bool success = runner.RunAll();

        KernelLog.Info($"[TestRunnerHost] Execution complete. Exit Code: {(success ? 0 : 1)}");
        
        // B13: Perform clean engine shutdown before process exit
        if (ArisenKernel.Lifecycle.EngineKernel.IsCreated)
        {
            ArisenKernel.Lifecycle.EngineKernel.Instance.Shutdown();
        }

        Environment.Exit(success ? 0 : 1);
    }
}

public class TestRunnerPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry services)
    {
        var runner = new TestRunnerService();
        services.RegisterService<ITestRunner>(runner);
        
        // Register the Application Host to take over the main thread
        var host = new TestRunnerHost(services);
        services.RegisterService<IApplicationHost>(host);
        
        KernelLog.Info("[TestRunner] Service and Host registered.");
    }

    public void OnUnload(IServiceRegistry services)
    {
    }
}
