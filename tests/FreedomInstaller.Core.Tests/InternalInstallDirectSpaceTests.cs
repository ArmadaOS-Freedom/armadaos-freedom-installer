using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Direct execution disk space budgets only the data the host saves, including the board and boot candidate
/// readbacks. The complete Linux boot and root readbacks are streamed into SHA-256.
/// <para>
/// This is policy unit verification only: the expected totals are independent literals, not a mirror of the
/// implementation, and no file, device or free-space probe is touched. It says nothing about a whole flow or
/// about physical space, and it deliberately does not repeat the separate Windows UNC production-method probe.
/// </para>
/// <para>
/// The context and its static helpers are internal and there is no <c>InternalsVisibleTo</c>, so this file uses
/// the narrow reflection bridge of the existing EX001 tests: resolve the exact static method by signature,
/// invoke it and rethrow the original exception via <see cref="ExceptionDispatchInfo"/>.
/// </para>
/// </summary>
public sealed class InternalInstallDirectSpaceTests
{
    // Independent oracle: 5808 bytes with the original capture, 3888 without that capture.
    private const long TotalWithCapture = 5808L;
    private const long TotalWithoutCapture = 3888L;
    private const long OriginalCaptureTotal = 1920L;

    private const long DummyRequiredBytes = 100L;

    [Fact]
    public void Budget_counts_the_original_capture_and_only_file_backed_readbacks()
    {
        Assert.Equal(TotalWithCapture, Budget(SampleInputs));
    }

    [Fact]
    public void Explicit_capture_switch_reproduces_the_default_total()
    {
        Assert.Equal(TotalWithCapture, Budget(includeOriginalCapture: true, SampleInputs));
        Assert.Equal(TotalWithCapture, Budget());
    }

    [Fact]
    public void Omitting_the_original_capture_removes_exactly_that_capture()
    {
        long without = Budget(includeOriginalCapture: false, SampleInputs);

        Assert.Equal(TotalWithoutCapture, without);
        Assert.Equal(OriginalCaptureTotal, TotalWithCapture - without);
    }

    [Fact]
    public void Overflow_of_a_positive_sum_is_refused_in_both_capture_modes()
    {
        long[] captureOverflow = (long[])SampleInputs.Clone();
        captureOverflow[0] = long.MaxValue; // gpt capture
        captureOverflow[1] = 1;             // boot_a capture
        Assert.Throws<InvalidDataException>(() => Budget(captureOverflow));

        long[] readbackOverflow = new long[10];
        readbackOverflow[7] = long.MaxValue;  // candidate A readback
        readbackOverflow[8] = 1;             // fresh misc readback
        Assert.Throws<InvalidDataException>(() => Budget(includeOriginalCapture: false, readbackOverflow));
    }

    [Fact]
    public void Any_negative_input_is_refused_before_any_sum()
    {
        long[] negativeCapture = (long[])SampleInputs.Clone();
        negativeCapture[2] = -1; // boot_b capture
        Assert.Throws<InvalidDataException>(() => Budget(negativeCapture));

        long[] negativeReadback = (long[])SampleInputs.Clone();
        negativeReadback[9] = -1; // overlay unit
        Assert.Throws<InvalidDataException>(() => Budget(includeOriginalCapture: false, negativeReadback));
    }

    [Fact]
    public void Insufficient_free_space_is_refused_with_the_stable_token()
    {
        InternalInstallWorkDiskSpaceException error = Assert.Throws<InternalInstallWorkDiskSpaceException>(
            () => RequireSpace(DummyRequiredBytes, 99L));

        Assert.Contains("insufficient-direct-execution-space", error.Message);
        Assert.Equal(DummyRequiredBytes, error.RequiredBytes);
        Assert.Equal(99L, error.AvailableBytes);
    }

    [Fact]
    public void Unknown_free_space_is_the_same_refusal_never_an_unbounded_permission()
    {
        InternalInstallWorkDiskSpaceException error = Assert.Throws<InternalInstallWorkDiskSpaceException>(
            () => RequireSpace(DummyRequiredBytes, null));

        Assert.Contains("insufficient-direct-execution-space", error.Message);
        Assert.Equal(DummyRequiredBytes, error.RequiredBytes);
        Assert.Null(error.AvailableBytes);
    }

    [Fact]
    public void Exact_free_space_is_allowed_while_a_non_positive_requirement_is_refused()
    {
        RequireSpace(DummyRequiredBytes, DummyRequiredBytes);

        Assert.Throws<InvalidDataException>(() => RequireSpace(0L, DummyRequiredBytes));
    }

    private static readonly long[] SampleInputs =
    {
        128L,  // gptCaptureBytes
        256L,  // bootACaptureBytes
        512L,  // bootBCaptureBytes
        1024L, // miscCaptureBytes
        32L,   // gptWriteBytes
        2048L, // boardReadbackBytes
        512L,  // candidateBBytes
        256L,  // candidateABytes
        1024L, // freshMiscBytes
        8L,    // overlayUnitBytes
    };

    private static readonly Type ContextType =
        typeof(InternalInstallBackend).Assembly.GetType(
            "FreedomInstaller.Core.InternalInstallPreparationContext",
            throwOnError: true)!;

    private static readonly MethodInfo DefaultBudget = RequireStaticMethod(
        "ComputeDirectExecutionSpace",
        BudgetSignature(includeCaptureSwitch: false));

    private static readonly MethodInfo SwitchBudget = RequireStaticMethod(
        "ComputeDirectExecutionSpace",
        BudgetSignature(includeCaptureSwitch: true));

    private static readonly MethodInfo RequireSpaceMethod = RequireStaticMethod(
        "RequireDirectExecutionSpaceAvailable",
        new[] { typeof(long), typeof(long?) });

    private static MethodInfo RequireStaticMethod(string name, Type[] signature) =>
        ContextType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, signature, null)
        ?? throw new InvalidOperationException(
            "InternalInstallPreparationContext no longer declares the internal static " + name
            + " method with the expected signature.");

    private static Type[] BudgetSignature(bool includeCaptureSwitch)
    {
        Type[] signature = new Type[includeCaptureSwitch ? 11 : 10];
        int firstValue = includeCaptureSwitch ? 1 : 0;

        if (includeCaptureSwitch)
        {
            signature[0] = typeof(bool);
        }

        for (int index = firstValue; index < signature.Length; index++)
        {
            signature[index] = typeof(long);
        }

        return signature;
    }

    private static long Budget() => (long)Invoke(DefaultBudget, BoxArguments(SampleInputs))!;

    private static long Budget(long[] values) => (long)Invoke(DefaultBudget, BoxArguments(values))!;

    private static long Budget(bool includeOriginalCapture, long[] values) =>
        (long)Invoke(SwitchBudget, WithCaptureSwitch(includeOriginalCapture, values))!;

    private static void RequireSpace(long requiredBytes, long? availableBytes) =>
        Invoke(RequireSpaceMethod, new object?[] { requiredBytes, availableBytes });

    private static object?[] BoxArguments(long[] values)
    {
        object?[] arguments = new object?[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            arguments[index] = values[index];
        }

        return arguments;
    }

    private static object?[] WithCaptureSwitch(bool includeOriginalCapture, long[] values)
    {
        object?[] boxed = BoxArguments(values);
        object?[] arguments = new object?[boxed.Length + 1];
        arguments[0] = includeOriginalCapture;
        Array.Copy(boxed, 0, arguments, 1, boxed.Length);
        return arguments;
    }

    private static object? Invoke(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
