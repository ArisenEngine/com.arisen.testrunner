using ArisenKernel.Diagnostics;

namespace ArisenEngine.Testing;

public static class SelfTests
{
    [ArisenTest("Verifies that the test runner can discover and execute managed tests.")]
    public static void VerifyTestRunner()
    {
        KernelLog.Info("[SelfTest] Test Runner is functional.");
    }
}
