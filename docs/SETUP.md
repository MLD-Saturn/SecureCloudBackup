# Azure Backup Tool - Setup Guide

## Overview

This is a zero-knowledge encrypted backup tool that syncs your local files to Microsoft Azure Blob Storage. All encryption happens locally on your machine before any data is uploaded, ensuring that no one (including Microsoft) can read your backed-up data.

## Cost Estimate

Based on your requirements (6TB of data, Cool tier storage):

| Component | Monthly Cost |
|-----------|--------------|
| Storage (6TB Cool tier) | ~$70 |
| Write operations | ~$2-5 |
| **Total** | **~$75-80/month** |

This is well under your $150/month budget, leaving room for growth.

---

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
   - **Redundancy**: **Locally-redundant storage (LRS)** - cheapest option
     - Or **Geo-redundant storage (GRS)** if you want disaster recovery across regions (adds ~$20/month for 6TB)

4. Click **Next: Advanced**
   - **Require secure transfer**: ? Enabled
   - **Enable blob public access**: ? Disabled
   - **Enable storage account key access**: ? Enabled
   - **Default access tier**: **Cool** (important for cost savings!)

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
2. Go to **Settings** tab
3. Enter your **Azure Connection String** (from Step 3)
4. Enter a **Container Name** (default: `backup`)
5. Click **Test Connection** to verify
6. Enter a **Password** (this derives your encryption key)
   
   ?? **IMPORTANT**: If you forget this password, your data **CANNOT** be recovered!

7. Add folders to watch for backup:
   - Click **Add Folder**
   - Browse to select folders
   - Optionally add exclude patterns (e.g., `*.tmp;node_modules;.git`)

8. Click **Save Settings**
9. Click **Initialize** to start

### Starting Backup

1. Go to **Dashboard**
2. Click **Start Backup** to begin real-time monitoring
3. Click **Full Scan** to immediately scan and backup all files in watched folders

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
| **Cool** | All backup data (default) |

Cool tier provides the best cost/performance balance for backup data that is:
- Written frequently
- Read rarely (disaster recovery only)

---

## Restore Operations

### Individual File Restore

1. Go to **Restore** tab
2. Click **Refresh** to list all backed-up files
3. Use **Search** to find specific files
4. Select a file and click **Restore Selected**
5. Choose destination folder

### Full Restore

1. Go to **Restore** tab
2. Enter a **Restore Directory**
3. Click **Restore All**
4. Wait for completion (this may take hours for large backups)

---

## Budget Monitoring

The application automatically monitors Azure costs:

- Current estimated cost is shown in the header
- If cost reaches 90% of budget: Warning displayed
- If cost exceeds budget: Backup pauses automatically and alerts you

To change budget:
1. Go to **Settings**
2. Modify **Monthly Budget**
3. Click **Save Settings**

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

### High costs
- Check if many files are changing frequently
- Large files that change often increase costs
- Consider excluding temporary files and caches

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
| Runtime | .NET 8 |
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

---

## License

MIT License - Free for personal and commercial use.
