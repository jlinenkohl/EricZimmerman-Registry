using System;
using System.Collections.Generic;
using System.Linq;
using NFluent;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Registry.Test;

/// <summary>
/// Minimal in-memory Serilog sink used to assert on emitted log messages without wiring up a full
/// logging test framework.
/// </summary>
internal sealed class CapturingSink : ILogEventSink
{
    public List<string> Messages { get; } = new();

    public void Emit(LogEvent logEvent)
    {
        Messages.Add(logEvent.RenderMessage());
    }
}

/// <summary>
/// Covers RegistryHive.ParseHive()'s end-of-hive sanity checks (only run when RecoverDeleted is true),
/// which compare the declared hive length against the number of bytes actually scanned. These checks
/// previously had two bugs that surfaced when scanning trailing bytes beyond the hive's declared
/// length (e.g. benign zero-padding, or the tail left over after Exceeds2GbRecovery truncation):
///   1. The zero-size-hbin/corruption skip steps always advanced by a full 4096 bytes, even when fewer
///      bytes than that remained, causing TotalBytesRead to overshoot past the actual scanned length.
///   2. The "Hive length does not equal bytes read" warning fired even when the only difference was
///      harmless all-zero trailing padding, which the adjacent check had already determined was benign.
/// </summary>
[TestFixture]
public class TestRegistryHiveTrailingDataWarnings
{
    private ILogger _originalLogger;
    private CapturingSink _sink;

    [SetUp]
    public void SetUp()
    {
        _originalLogger = Log.Logger;
        _sink = new CapturingSink();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        Log.Logger = _originalLogger;
    }

    [Test]
    public void TrailingZeroPaddingWithRecoverDeletedShouldNotWarnAboutErroneousData()
    {
        var rawBytes = System.IO.File.ReadAllBytes(@"./Hives/SAM");

        // Add a small amount of benign zero padding beyond the hive's declared length.
        Array.Resize(ref rawBytes, rawBytes.Length + 0x500);

        var hive = new RegistryHive(rawBytes, @"./Hives/SAM_padded");
        hive.RecoverDeleted = true;
        hive.FlushRecordListsAfterParse = false;

        Check.ThatCode(() => hive.ParseHive()).DoesNotThrow();

        Check.That(_sink.Messages.Any(m => m.Contains("does not equal bytes read"))).IsFalse();
        Check.That(_sink.Messages.Any(m => m.Contains("Extra, non-zero data found"))).IsFalse();
    }

    [Test]
    public void TrailingNonZeroDataWithRecoverDeletedShouldStillWarn()
    {
        var rawBytes = System.IO.File.ReadAllBytes(@"./Hives/SAM");

        var originalLength = rawBytes.Length;
        Array.Resize(ref rawBytes, originalLength + 0x400);
        for (var i = originalLength; i < rawBytes.Length; i++)
        {
            rawBytes[i] = 0xAB;
        }

        var hive = new RegistryHive(rawBytes, @"./Hives/SAM_garbage_tail");
        hive.RecoverDeleted = true;
        hive.FlushRecordListsAfterParse = false;

        Check.ThatCode(() => hive.ParseHive()).DoesNotThrow();

        Check.That(_sink.Messages.Any(m => m.Contains("Extra, non-zero data found"))).IsTrue();
        Check.That(_sink.Messages.Any(m => m.Contains("does not equal bytes read"))).IsTrue();
    }

    [Test]
    public void TotalBytesReadShouldNeverExceedHiveLength()
    {
        var rawBytes = System.IO.File.ReadAllBytes(@"./Hives/SAM");
        Array.Resize(ref rawBytes, rawBytes.Length + 0x500);

        var hive = new RegistryHive(rawBytes, @"./Hives/SAM_padded");
        hive.RecoverDeleted = true;
        hive.FlushRecordListsAfterParse = false;

        hive.ParseHive();

        Check.That(hive.TotalBytesRead <= hive.FileBytes.Length).IsTrue();
    }
}
