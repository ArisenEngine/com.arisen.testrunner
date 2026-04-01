using System;

namespace ArisenEngine.Testing;

/// <summary>
/// Service for discovering and running tests within the Arisen Engine.
/// </summary>
public interface ITestRunner
{
    /// <summary>
    /// Executes all discovered tests in the loaded packages.
    /// </summary>
    /// <returns>True if all tests passed, false otherwise.</returns>
    bool RunAll();

    /// <summary>
    /// Registers a manual test execution node (e.g. for native tests).
    /// </summary>
    void RegisterNativeTest(string name, Action action);
}
