using System;

namespace ArisenEngine.Testing;

/// <summary>
/// Marks a method as a test case for discovery by the Arisen Test Runner.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class ArisenTestAttribute : Attribute
{
    public string Description { get; }

    public ArisenTestAttribute(string description = "")
    {
        Description = description;
    }
}
