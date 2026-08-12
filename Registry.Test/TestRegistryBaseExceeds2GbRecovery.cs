using System;
using System.IO;
using NFluent;
using NUnit.Framework;
using Registry.Other;

namespace Registry.Test;

/// <summary>
/// Covers the >=2GB hive loading path (RegistryParseSettings.Exceeds2GbRecovery), including the
/// regression scenario where a hive file size clamped to int.MaxValue (0x7FFFFFFF) caused
/// "Array dimensions exceeded supported range" because the CLR's actual max array length is
/// RegistryBase.MaxByteArrayLength (0x7FFFFFC7), 88 bytes smaller than int.MaxValue.
/// </summary>
[TestFixture]
public class TestRegistryBaseExceeds2GbRecovery
{
    private bool _originalExceeds2GbRecovery;
    private bool _originalContinueOnCorruption;
    private string _originalCorruptionLogPath;
    private string _tempDirectory;

    [SetUp]
    public void SetUp()
    {
        _originalExceeds2GbRecovery = RegistryParseSettings.Exceeds2GbRecovery;
        _originalContinueOnCorruption = RegistryParseSettings.ContinueOnCorruption;
        _originalCorruptionLogPath = RegistryParseSettings.CorruptionLogPath;

        _tempDirectory = Path.Combine(Path.GetTempPath(), "RegistryTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        RegistryParseSettings.Exceeds2GbRecovery = _originalExceeds2GbRecovery;
        RegistryParseSettings.ContinueOnCorruption = _originalContinueOnCorruption;
        RegistryParseSettings.CorruptionLogPath = _originalCorruptionLogPath;

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Creates a sparse copy of a small, valid hive whose on-disk length is <paramref name="totalLength"/>
    /// bytes. The real hive bytes (header + content) are written at the start of the file, and the file is
    /// then extended (as a sparse hole) to the requested length, without allocating that space on disk.
    /// </summary>
    private string CreateSparseHiveCopy(string sourceHivePath, long totalLength)
    {
        var destPath = Path.Combine(_tempDirectory, Path.GetFileName(sourceHivePath) + "_sparse");

        File.Copy(sourceHivePath, destPath, true);

        using (var fs = new FileStream(destPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(totalLength);
        }

        return destPath;
    }

    [Test]
    public void HiveJustOverMaxByteArrayLengthWithRecoveryEnabledShouldLoadWithoutArrayDimensionException()
    {
        RegistryParseSettings.Exceeds2GbRecovery = true;

        var hivePath = CreateSparseHiveCopy(@"./Hives/SAM", (long)RegistryBase.MaxByteArrayLength + 0x2000);

        RegistryBase r = null;
        Check.ThatCode(() => { r = new RegistryBase(hivePath); }).DoesNotThrow();

        Check.That(r.FileBytes.Length).IsEqualTo(RegistryBase.MaxByteArrayLength);
        Check.That(r.HiveType).IsEqualTo(HiveTypeEnum.Sam);
    }

    [Test]
    public void HiveJustOverMaxByteArrayLengthWithRecoveryDisabledShouldThrowArgumentOutOfRangeException()
    {
        RegistryParseSettings.Exceeds2GbRecovery = false;

        var hivePath = CreateSparseHiveCopy(@"./Hives/SAM", (long)RegistryBase.MaxByteArrayLength + 0x2000);

        Check.ThatCode(() => { _ = new RegistryBase(hivePath); }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public void HiveAtExactly2GbBoundaryWithRecoveryEnabledShouldReproduceReportedBugScenario()
    {
        // This mirrors the field-reported failure: a hive file exactly 0x80000000 (2GB) in size.
        RegistryParseSettings.Exceeds2GbRecovery = true;

        var hivePath = CreateSparseHiveCopy(@"./Hives/SAM", 0x80000000L);

        RegistryBase r = null;
        Check.ThatCode(() => { r = new RegistryBase(hivePath); }).DoesNotThrow();

        Check.That(r.FileBytes.Length).IsEqualTo(RegistryBase.MaxByteArrayLength);
        Check.That(r.HiveType).IsEqualTo(HiveTypeEnum.Sam);
    }

    [Test]
    public void PaddingShouldExtendFileBytesWhenDeclaredHiveLengthExceedsClampedReadLength()
    {
        // Simulate a hive capture that was truncated on disk (only the first 0x5000 bytes are present),
        // while the header still declares the original, larger hive length. Exceeds2GbRecovery padding
        // should extend FileBytes out to the declared length and record the corruption.
        RegistryParseSettings.Exceeds2GbRecovery = true;

        var corruptionLog = Path.Combine(_tempDirectory, "corruption.log");
        RegistryParseSettings.CorruptionLogPath = corruptionLog;

        var hivePath = Path.Combine(_tempDirectory, "SAM_truncated");
        using (var source = File.OpenRead(@"./Hives/SAM"))
        using (var dest = File.Create(hivePath))
        {
            var buffer = new byte[0x5000];
            var read = source.Read(buffer, 0, buffer.Length);
            dest.Write(buffer, 0, read);
        }

        var declaredHiveLength = BitConverter.ToUInt32(File.ReadAllBytes(@"./Hives/SAM"), 0x28);
        var expectedDeclaredFileLength = declaredHiveLength + 0x1000UL;

        RegistryBase r = null;
        Check.ThatCode(() => { r = new RegistryBase(hivePath); }).DoesNotThrow();

        Check.That((ulong)r.FileBytes.Length).IsEqualTo(expectedDeclaredFileLength);
        Check.That(File.Exists(corruptionLog)).IsTrue();
        Check.That(File.ReadAllText(corruptionLog)).Contains("padded hive bytes");
    }

    [Test]
    public void PaddingShouldWarnAndSkipResizeWhenDeclaredHiveLengthExceedsMaxByteArrayLength()
    {
        // Craft a hive byte array whose header declares an (invalid/corrupt) hive length so large that
        // padding to it would itself exceed the CLR's max array length. The padding step must not throw;
        // it should log a warning and leave FileBytes unmodified.
        RegistryParseSettings.Exceeds2GbRecovery = true;

        var corruptionLog = Path.Combine(_tempDirectory, "corruption.log");
        RegistryParseSettings.CorruptionLogPath = corruptionLog;

        var rawBytes = File.ReadAllBytes(@"./Hives/SAM");

        // Overwrite the declared hive length field (offset 0x28) with an oversized value.
        var oversizedLength = BitConverter.GetBytes(uint.MaxValue);
        Array.Copy(oversizedLength, 0, rawBytes, 0x28, 4);

        var originalLength = rawBytes.Length;

        RegistryBase r = null;
        Check.ThatCode(() => { r = new RegistryBase(rawBytes, @"./Hives/SAM"); }).DoesNotThrow();

        Check.That(r.FileBytes.Length).IsEqualTo(originalLength);
        Check.That(File.Exists(corruptionLog)).IsTrue();
        Check.That(File.ReadAllText(corruptionLog)).Contains("exceeds byte-array parser limit");
    }

    [Test]
    public void PaddingShouldBeNoOpWhenRecoveryDisabled()
    {
        RegistryParseSettings.Exceeds2GbRecovery = false;

        var hivePath = Path.Combine(_tempDirectory, "SAM_truncated");
        using (var source = File.OpenRead(@"./Hives/SAM"))
        using (var dest = File.Create(hivePath))
        {
            var buffer = new byte[0x5000];
            var read = source.Read(buffer, 0, buffer.Length);
            dest.Write(buffer, 0, read);
        }

        var r = new RegistryBase(hivePath);

        Check.That(r.FileBytes.Length).IsEqualTo(0x5000);
    }
}
