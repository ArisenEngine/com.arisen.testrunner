using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using ArisenKernel.Services;
using ArisenKernel.Packages;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;

using System.Runtime.InteropServices;

namespace ArisenEngine.Testing;

public class TestRunnerService : ITestRunner
{
    private readonly Dictionary<string, Action> _nativeTests = new();

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
            if (assembly.FullName.StartsWith("System") || assembly.FullName.StartsWith("Microsoft"))
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
        // In a real Arisen engine, we would discover all native DLLs from the active packages.
        // For this proof-of-concept, we'll try to load the Vulkan Test DLL specifically.
        string dllName = "Arisen.RHI.Vulkan.Test.dll";
        IntPtr hModule = LoadLibrary(dllName);
        if (hModule == IntPtr.Zero)
        {
            KernelLog.Info($"[TestRunner] Native test library {dllName} not found or failed to load.");
            return;
        }

        IntPtr funcPtr = GetProcAddress(hModule, "RegisterNativeTests");
        if (funcPtr == IntPtr.Zero)
        {
            KernelLog.Warning($"[TestRunner] Native function 'RegisterNativeTests' not found in {dllName}.");
            return;
        }

        var registerFunc = Marshal.GetDelegateForFunctionPointer<NativeRegisterFunc>(funcPtr);
        
        // Callback used by C++ to register its tests in the managed dict
        RegisterCallback callback = (name, actionPtr) =>
        {
            var action = Marshal.GetDelegateForFunctionPointer<Action>(actionPtr);
            RegisterNativeTest(name, action);
        };

        registerFunc(callback);
    }
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
