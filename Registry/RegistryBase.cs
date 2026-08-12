using System;
using System.IO;
using System.Linq;
using System.Text;
using Registry.Other;
using Serilog;
using static Registry.Other.Helpers;

namespace Registry;

public class RegistryBase : IRegistry
{
    private readonly string _streamBackedPath;
    private readonly bool _useStreamBackedReads;

    public RegistryBase()
    {
        throw new NotSupportedException("Call the other constructor and pass in the path to the Registry hive!");
    }

    public RegistryBase(byte[] rawBytes, string hivePath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        FileBytes = rawBytes;
        HiveFileLength = rawBytes.LongLength;
        HivePath = "None";


        if (!HasValidSignature())
        {
            Log.Error("Data in byte array is not a Registry hive (bad signature)");

            throw new ArgumentException("Data in byte array is not a Registry hive (bad signature)");
        }

        HivePath = hivePath;

        Initialize();
    }

    public RegistryBase(string hivePath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (hivePath == null) throw new ArgumentNullException("hivePath cannot be null");

        if (!File.Exists(hivePath))
        {
            var fullPath = Path.GetFullPath(hivePath);
            throw new FileNotFoundException($"The specified file {fullPath} was not found.", fullPath);
        }

        using (var fileStream = new FileStream(hivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            HiveFileLength = fileStream.Length;

            if (RegistryParseSettings.Exceeds2GbRecovery && fileStream.Length > int.MaxValue)
            {
                _useStreamBackedReads = true;
                _streamBackedPath = hivePath;

                var bootstrapLen = (int) Math.Min(4096L, fileStream.Length);
                FileBytes = new byte[bootstrapLen];
                fileStream.Seek(0, SeekOrigin.Begin);
                _ = fileStream.Read(FileBytes, 0, bootstrapLen);
            }
            else
            {
                var binaryReader = new BinaryReader(fileStream);
                binaryReader.BaseStream.Seek(0, SeekOrigin.Begin);
                FileBytes = binaryReader.ReadBytes((int) binaryReader.BaseStream.Length);
            }
        }


        if (!HasValidSignature())
        {
            Log.Error("{HivePath} is not a Registry hive (bad signature)", hivePath);

            throw new Exception($"{hivePath} is not a Registry hive (bad signature)");
        }

        HivePath = hivePath;

        //    Logger.Trace("Set HivePath to {0}", hivePath);

        Initialize();
    }

    public long TotalBytesRead { get; internal set; }
    public string Version { get; private set; }
    public long HiveFileLength { get; protected set; }

    public byte[] FileBytes { get; internal set; }

    public HiveTypeEnum HiveType { get; private set; }

    public string HivePath { get; }

    public RegistryHeader Header { get; set; }

    public byte[] ReadBytesFromHive(long offset, int length)
    {
        if (_useStreamBackedReads)
        {
            if (offset < 0 || length <= 0)
            {
                return new byte[0];
            }

            var streamReadLength = Math.Abs(length);

            if (offset >= HiveFileLength)
            {
                return new byte[0];
            }

            var streamRemaining = HiveFileLength - offset;
            if (streamRemaining <= 0)
            {
                return new byte[0];
            }

            if (streamReadLength > streamRemaining)
            {
                streamReadLength = (int) streamRemaining;
            }

            var result = new byte[streamReadLength];

            using (var fs = new FileStream(_streamBackedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var bytesRead = fs.Read(result, 0, streamReadLength);
                if (bytesRead < streamReadLength)
                {
                    Array.Resize(ref result, bytesRead);
                }
            }

            return result;
        }

        var readLength = Math.Abs(length);

        var remaining = FileBytes.Length - offset;

        if (remaining <= 0) return new byte[0];

        if (readLength > remaining) readLength = (int) remaining;

        var r = new ArraySegment<byte>(FileBytes, (int) offset, readLength);

        return r.ToArray();
    }

    internal void Initialize()
    {
        var header = ReadBytesFromHive(0, 4096);

        //    Logger.Trace("Getting header");

        Header = new RegistryHeader(header);

        var fileNameSegs = Header.FileName.Split('\\');

        var fNameBase = fileNameSegs.Last().ToLowerInvariant();
        
        Log.Debug("Got hive header. Embedded file name {FileName}. Base Name {Base}", Header.FileName,fNameBase);

        switch (fNameBase)
        {
            case "ntuser.dat":
                HiveType = HiveTypeEnum.NtUser;
                break;
            case "sam":
                HiveType = HiveTypeEnum.Sam;
                break;
            case "security":
                HiveType = HiveTypeEnum.Security;
                break;
            case "software":
                HiveType = HiveTypeEnum.Software;
                break;
            case "system":
                HiveType = HiveTypeEnum.System;
                break;
            case "drivers":
                HiveType = HiveTypeEnum.Drivers;
                break;
            case "usrclass.dat":
                HiveType = HiveTypeEnum.UsrClass;
                break;
            case "components":
                HiveType = HiveTypeEnum.Components;
                break;
            case "bcd":
                HiveType = HiveTypeEnum.Bcd;
                break;
            case "amcache.hve":
            case "amcache.hve.tmp":
                HiveType = HiveTypeEnum.Amcache;
                break;
            case "syscache.hve":
                HiveType = HiveTypeEnum.Syscache;
                break;
            case "elam": 
                HiveType = HiveTypeEnum.Elam;
                break;
            case "default": 
                HiveType = HiveTypeEnum.Default;
                break;
            case "Vsmidk": 
                HiveType = HiveTypeEnum.Vsmidk;
                break;
            case "BcdTemplate":
                HiveType = HiveTypeEnum.BcdTemplate;
                break;
            case "bbi": 
                HiveType = HiveTypeEnum.Bbi;
                break;
            case "userdiff": 
                HiveType = HiveTypeEnum.Userdiff;
                break;
            case "user.dat": 
                HiveType = HiveTypeEnum.User;
                break;
            case "userclasses.dat": 
                HiveType = HiveTypeEnum.UserClasses;
                break;
            case "settings.dat": 
                HiveType = HiveTypeEnum.settings;
                break;
            case "registry.dat": 
                HiveType = HiveTypeEnum.Registry;
                break;
            default:
                HiveType = HiveTypeEnum.Other;
                break;
        }

        //    Logger.Trace("Hive is a {0} hive", HiveType);

        Version = $"{Header.MajorVersion}.{Header.MinorVersion}";

        //   Logger.Trace("Hive version is {0}", version);
    }

    public bool HasValidSignature()
    {
        if (FileBytes.Length < 4)
        {
            return false;
        }

        var sig = BitConverter.ToInt32(FileBytes, 0);

        return sig.Equals(RegfSignature);
    }

    protected internal long HiveLength()
    {
        return _useStreamBackedReads ? HiveFileLength : FileBytes.LongLength;
    }
}
