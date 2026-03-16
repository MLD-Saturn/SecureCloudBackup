# Azure Backup Tool - Setup Guide

## Overview

This is a zero-knowledge encrypted backup tool that syncs your local files to Microsoft Azure Blob Storage. All encryption happens locally on your machine before any data is uploaded, ensuring that no one (including Microsoft) can read your backed-up data.

## Azure Setup Instructions

### Step 1: Create a Resource Group

1. Go to [Azure Portal](https://portal.azure.com)
2. Search for "Resource groups" in the top search bar
3. Click **+ Create**
4. Fill in:
   - **Subscription**: Select your subscription
   - **Resource group**: `rg-backup` (or your preferred name)
   - **Region**: Choose a region close to you (e.g., `East US`, `West Europe`)
5. Click **Review + create** ? **Create**

### Step 2: Create a Storage Account

1. Search for "Storage accounts" in the Azure portal
2. Click **+ Create**
3. Fill in the **Basics** tab:
   - **Subscription**: Select your subscription
   - **Resource group**: Select `rg-backup`
   - **Storage account name**: `stbackup<yourname>` (must be globally unique, lowercase, no special characters)
   - **Region**: Same region as your resource group
   - **Performance**: **Standard** (not Premium)
   - **Redundancy**: **Locally-redundant storage (LRS)**
     - Or **Geo-redundant storage (GRS)** if you want disaster recovery across regions

4. Click **Next: Advanced**
   - **Require secure transfer**: ? Enabled
   - **Enable blob public access**: ? Disabled
   - **Enable storage account key access**: ? Enabled
   - **Default access tier**: **Cool** (recommended for backup workloads)

5. Click **Review + create** ? **Create**

### Step 3: Get the Connection String

1. Go to your newly created Storage Account
2. In the left menu, under **Security + networking**, click **Access keys**
3. Click **Show** next to the first key
4. Copy the **Connection string** (it looks like: `DefaultEndpointsProtocol=https;AccountName=...`)
5. Save this securely - you'll need it to configure the backup tool

---

## Application Setup

### Running the Application

1. **From USB/Portable:**
   - Copy the entire `AzureBackup` folder to your USB drive
   - Run `AzureBackup.exe`

2. **Publishing as Single File (for maximum portability):**
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```
   For other platforms:
   - macOS: `-r osx-x64` or `-r osx-arm64` (Apple Silicon)
   - Linux: `-r linux-x64`

### Initial Configuration

1. Launch the application
2. In the **Settings** view, choose an authentication method:
   - **Connection String** -- paste the string from Azure Portal (from Step 3)
   - **Microsoft Entra ID** -- click **Sign in with Microsoft** and enter your storage account name
3. Enter a **Container Name** (default: `backup`)
4. Click **Test Connection** to verify
5. Enter a **Password** (this derives your encryption key)

   **IMPORTANT**: If you forget this password, your data **CANNOT** be recovered!

6. Click **Initialize & Connect** to encrypt the database, save settings, and connect

### Adding Folders and Starting Backup

1. Go to the **Sync** view
2. Click the **+** button in the Local Files panel to add watched folders
3. Click **Start Monitoring** to begin real-time file watching and automatic backup

---

## How It Works

### Encryption (Zero-Knowledge)

```
Your Password 
     ?
[Argon2id Key Derivation] ? Salt (stored locally)
     ?
256-bit AES Key
     ?
[AES-256-GCM Encryption] ? Random nonce per chunk
     ?
Encrypted Data ? Azure Blob Storage
```

- **Argon2id**: Memory-hard key derivation (resistant to GPU attacks)
- **AES-256-GCM**: Authenticated encryption (tamper-proof)
- **Random nonce**: Each chunk has a unique nonce
- **Zero-knowledge**: Azure only sees encrypted blobs with meaningless names

### Delta Sync (Bandwidth Optimization)

For large files, the tool uses Content-Defined Chunking (CDC):

1. Files are split into variable-sized chunks (64KB - 1MB)
2. Each chunk is hashed
3. Only chunks that have changed are uploaded
4. Existing chunks are deduplicated (content-addressable storage)

This means if you modify a 1GB file, only the changed portions are uploaded.

### Storage Tiers

| Tier | Used For |
|------|----------|
| **Hot** | Frequently accessed data |
| **Cool** | Backup data (default, recommended) |
| **Cold** | Rarely accessed, long-term archival data |

Each watched folder can be configured with a different storage tier in Settings. Cool tier provides a good balance for backup data that is:
- Written frequently
- Read rarely (disaster recovery only)

---

## Restore Operations

### Individual File Restore

1. Go to the **Sync** view
2. In the **Azure Backup** panel (right side), check the files to restore
3. Use the **Search** bar to filter by filename
4. Choose **Restore to original location** or click **Browse...** for a different destination
5. Click **Restore Selected**
6. Review the preview dialog and click **Proceed**

### Full Folder Restore

1. In the Azure Backup tree, select a folder
2. Use the **Remap path** panel to set a target directory
3. Click **Mirror Sync** to restore all files (and optionally remove extra local files)
4. Review the preview and click **Proceed**

---

## Troubleshooting

### "Connection failed"
- Verify your connection string is correct
- Check that your firewall allows HTTPS (port 443) to Azure
- Ensure the storage account exists and is accessible

### "Invalid password"
- The password must match exactly what was used during initial setup
- Passwords are case-sensitive
- There is no password recovery - if forgotten, data is lost

### "File locked"
- The application waits up to 5 minutes for locked files
- If a file remains locked, it will be skipped
- Close applications using the file, or exclude it from backup

---

## Security Best Practices

1. **Use a strong password**: At least 16 characters, mix of letters, numbers, symbols
2. **Don't store password digitally**: Memorize it or use a physical backup
3. **Secure your connection string**: Treat it like a password
4. **Regular test restores**: Periodically verify you can restore files
5. **Keep local database backed up**: The `backup.db` file contains metadata

---

## Technical Specifications

| Component | Technology |
|-----------|------------|
| Runtime | .NET 10 |
| GUI | Avalonia UI 11 |
| Encryption | AES-256-GCM |
| Key Derivation | Argon2id (64MB memory, 3 iterations) |
| Local Database | LiteDB |
| Chunking | Content-Defined Chunking (CDC) |
| Azure SDK | Azure.Storage.Blobs 12.x |

---

## File Locations

| File | Location | Purpose |
|------|----------|---------|
| `backup.db` | `%LOCALAPPDATA%\AzureBackup\` | Local metadata database |
| Configuration | Inside `backup.db` | Azure connection, watched folders |

---

## Support

For issues or feature requests, please open an issue on the repository.
