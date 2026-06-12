using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Management.Automation;
using System.Collections.Generic;

namespace Subsystem.Tools.CodeContext.Cmdlets;

[Cmdlet(VerbsCommon.Get, "GitGraphContext")]
public class GetGitGraphContextCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = false)]
    public string GitRoot { get; set; } = Directory.GetCurrentDirectory();

    protected override void ProcessRecord()
    {
        string gitFolder = Path.Combine(GitRoot, ".git");
        if (!Directory.Exists(gitFolder))
        {
            WriteWarning("No .git folder found. Bypassing git graph introspection.");
            return;
        }

        string indexPath = Path.Combine(gitFolder, "index");
        if (!File.Exists(indexPath))
        {
            WriteWarning($"Git index file not found at: {indexPath}");
            return;
        }

        try
        {
            using var stream = File.OpenRead(indexPath);
            using var reader = new BinaryReader(stream);
            
            if (stream.Length < 12)
            {
                WriteWarning("Git index file is too short.");
                return;
            }

            // Validate signature "DIRC"
            byte[] sig = reader.ReadBytes(4);
            string sigStr = Encoding.ASCII.GetString(sig);
            if (sigStr != "DIRC")
            {
                WriteError(new ErrorRecord(new InvalidDataException("Invalid Git index signature"), "InvalidIndexSig", ErrorCategory.InvalidData, indexPath));
                return;
            }

            uint version = ReadUInt32BE(reader);
            uint entryCount = ReadUInt32BE(reader);
            
            WriteVerbose($"Reading Git Index Version {version} containing {entryCount} entries...");

            var results = new List<object>();

            // Parse entries
            for (int i = 0; i < entryCount; i++)
            {
                // Each entry has:
                // 8 bytes ctime, 8 bytes mtime, 4 bytes dev, 4 bytes ino, 
                // 4 bytes mode, 4 bytes uid, 4 bytes gid, 4 bytes file size
                // Total metadata to skip = 40 bytes
                if (stream.Position + 62 > stream.Length)
                {
                    WriteWarning("Unexpected end of index stream while parsing entry.");
                    break;
                }

                stream.Seek(40, SeekOrigin.Current);
                
                byte[] sha = reader.ReadBytes(20);
                ushort flags = ReadUInt16BE(reader);
                
                // Length is lowest 12 bits of flags
                int nameLength = flags & 0x0FFF;
                
                if (stream.Position + nameLength > stream.Length)
                {
                    WriteWarning("Unexpected end of index stream while reading entry path.");
                    break;
                }

                byte[] nameBytes = reader.ReadBytes(nameLength);
                string name = Encoding.UTF8.GetString(nameBytes);
                
                // Skip padding null bytes (index entries are padded to 8-byte boundaries)
                int entrySize = 62 + nameLength;
                int pad = 8 - (entrySize % 8);
                stream.Seek(pad, SeekOrigin.Current);

                string shaHex = BitConverter.ToString(sha).Replace("-", "").ToLowerInvariant();
                results.Add(new { Path = name, Sha = shaHex });
            }

            WriteObject(results.ToArray(), true);
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "ParseIndexFailed", ErrorCategory.ReadError, indexPath));
        }
    }

    private static uint ReadUInt32BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static ushort ReadUInt16BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(2);
        Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }
}
