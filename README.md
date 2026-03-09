# Azure Backup Tool

A zero-knowledge encrypted backup tool that syncs your files to Microsoft Azure Blob Storage with client-side encryption.

## Features

- **?? Zero-Knowledge Encryption**: All data is encrypted locally using AES-256-GCM before upload. Your password never leaves your machine.
- **? Delta Sync**: Content-defined chunking uploads only changed portions of files, minimizing bandwidth and costs.
- **??? Real-Time Monitoring**: File system watcher detects changes and backs up automatically.
- **?? Cost Aware**: Built-in budget monitoring stops backups if costs exceed your limit.
- **?? Easy Restore**: Restore individual files or perform full recovery.
- **??? Cross-Platform**: Runs on Windows, macOS, and Linux.
- **?? Portable**: Single executable, no installation required.

## Quick Start

### 1. Create Azure Storage Account
Follow the [Azure Setup Guide](docs/SETUP.md) to create a storage account.

### 2. Get the Application
```bash
# Build from source
dotnet publish src/AzureBackup -c Release -r win-x64 --self-contained true -o publish

# Or use the build script
./build-portable.cmd   # Windows
./build-portable.sh    # Linux/macOS
```

### 3. First-Time Configuration

1. **Launch the application** and go to the **Settings** tab
2. **Enter your Azure Connection String** (from Azure Portal ? Storage Account ? Access Keys)
3. **Set your encryption password** and confirm it
   > ?? **CRITICAL**: This password cannot be recovered. Store it safely!
4. **Add folders to watch** by clicking "Add Folder"
5. **(Optional)** Configure exclusion patterns: `*.tmp;*.log;node_modules;.git`
6. Click **Save Settings**, then **Initialize**

### 4. Start Backup

1. Go to the **Dashboard** tab
2. Click **? Start Monitoring**
3. The app automatically scans your folders and begins backing up

That's it! The app will now monitor your folders and backup changes automatically.

?? **For detailed instructions, see the [User Guide](docs/USER_GUIDE.md)**

## Building

```bash
# Clone the repository
git clone <repo-url>
cd azurebackup

# Build
dotnet build

# Run in development
dotnet run --project src/AzureBackup

# Publish portable executable (Windows)
dotnet publish src/AzureBackup -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Run tests
dotnet test
```

## Cost Estimate

For 6TB of data using Cool tier:
- **Storage**: ~$70/month
- **Operations**: ~$5/month
- **Total**: ~$75-80/month

## Architecture

```
???????????????????????????????????????????????
?             Your Computer                    ?
?  ?????????????????????????????????????????  ?
?  ?         Azure Backup Tool              ?  ?
?  ?                                        ?  ?
?  ?  [File Watcher] ? [Chunking Service]   ?  ?
?  ?         ?              ?               ?  ?
?  ?  [Encryption] ? Password Derived Key   ?  ?
?  ?         ?                              ?  ?
?  ?  [Azure Blob Service]                  ?  ?
?  ?????????????????????????????????????????  ?
?                    ?                         ?
????????????????????????????????????????????????
                     ? HTTPS (TLS 1.3)
                     ?
???????????????????????????????????????????????
?           Azure Blob Storage                 ?
?  ?????????????????????????????????????????  ?
?  ?   Encrypted Blobs (unreadable)        ?  ?
?  ?   • chunks/HASH123...                 ?  ?
?  ?   • chunks/HASH456...                 ?  ?
?  ?   • metadata/FILE_PATH_HASH...        ?  ?
?  ?????????????????????????????????????????  ?
???????????????????????????????????????????????
```

## Security

### Encryption
- **Key Derivation**: Argon2id with 64MB memory, 3 iterations, 8-way parallelism
- **Encryption**: AES-256-GCM with random nonce per chunk
- **Integrity**: CRC32 checksum + GCM authentication tag on all encrypted data
- **Format Versioning**: Version byte in encrypted data for future compatibility

### Zero-Knowledge Architecture
- Azure sees only encrypted blobs with SHA-256 hashed names
- Connection strings are encrypted at rest using your password
- No recovery possible without password (by design)

### Data Integrity
- SHA-256 hashes for chunk content addressing (collision resistant)
- Chunk sequence validation on restore
- File hash verification after restore completes
- Atomic writes using temp files

### Security Hardening
- Rate limiting on password attempts (lockout after 5 failures)
- Exponential backoff on lockouts (15 min, 30 min, 60 min, ...)
- Blob name validation to prevent path traversal
- Thread-safe encryption key handling with memory zeroing
- Input validation on all public APIs

### Reliability
- Database transactions for atomic operations
- FileSystemWatcher buffer overflow recovery with automatic rescan
- Regex timeout protection against ReDoS attacks
- Proper error handling with custom exception types

## Documentation

- [Full Setup Guide](docs/SETUP.md)

## License

MIT License
