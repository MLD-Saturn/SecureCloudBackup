# Azure Backup Tool - User Guide

This guide explains how to use the Azure Backup Tool for common backup and restore scenarios.

## Table of Contents

1. [First-Time Setup](#first-time-setup)
2. [Returning Users (Daily Unlock)](#returning-users-daily-unlock)
3. [Daily Operations](#daily-operations)
4. [Restoring Files](#restoring-files)
5. [Managing Watched Folders](#managing-watched-folders)
6. [Understanding the Dashboard](#understanding-the-dashboard)
7. [Troubleshooting](#troubleshooting)

---

## First-Time Setup

When you first launch the app, you'll see "Not Configured" status.

### Step 1: Configure Azure Connection

1. Open the application and click **Settings** in the navigation bar
2. In the **Azure Connection** section:
   - Paste your Azure Storage **Connection String** (get this from Azure Portal ? Storage Account ? Access Keys)
   - Enter a **Container Name** (default is "backup")
   - Click **Test Connection** to verify it works

> ?? **Tip**: Click the ?? button to reveal the connection string if you need to verify it.

### Step 2: Set Your Encryption Password

1. In the **Set Encryption Password** section:
   - Enter a **strong password** (this encrypts all your data)
   - **Confirm the password** by typing it again
   - Watch for the green checkmark or red warning indicating match status

> ?? **CRITICAL**: If you forget this password, your data **CANNOT** be recovered. There is no password reset. Store it safely!

### Step 3: Add Folders to Watch

1. In the **Watched Folders** section:
   - Click **Add Folder** to open the folder picker
   - Select a folder you want to backup
   - The folder appears in the list with a checkbox (enabled by default)

2. **Configure Exclusions** (optional but recommended):
   - Click on a folder in the list to select it
   - In the **Exclusion Patterns** section below, enter patterns to skip
   - Separate multiple patterns with semicolons
   - Common patterns: `*.tmp;*.log;node_modules;.git;bin;obj;.vs`

### Step 4: Initialize and Start

1. Click **Save Settings** to save your configuration
2. Click **?? Initialize** to:
   - Verify your password
   - Connect to Azure
   - Encrypt and store your connection string securely
3. You'll see "? Unlocked" when successful

4. Go to the **Dashboard** and click **? Start Monitoring**
   - The app will automatically run a **Full Scan** to discover files
   - Files will begin uploading to Azure

---

## Returning Users (Daily Unlock)

When you return to the app after closing it, you'll see "Locked (Enter Password)" status.

### Quick Unlock Process

1. Open the application
2. Go to **Settings** tab
3. You'll see the simplified **Unlock (Enter Your Password)** section
4. Enter your password
5. Click **?? Unlock**
6. You'll see "? Unlocked" when successful

> ?? **Note**: Your Azure connection and folders are already saved. You only need to enter your password to unlock.

### Start Monitoring

1. Go to **Dashboard**
2. Click **? Start Monitoring**
3. The status indicator turns green showing "Monitoring for Changes"

---

## Daily Operations

| Status | Indicator | Meaning |
|--------|-----------|---------|
| **Monitoring for Changes** | ?? Green | Actively watching folders and backing up changes |
| **Ready (Not Monitoring)** | ? Gray | Initialized but not watching for changes |
| **Not Configured** | ? Gray | Need to configure settings and initialize |

### Manual Backup Operations

| Action | When to Use |
|--------|-------------|
| **?? Full Scan** | Run after adding new folders, or to ensure everything is backed up |
| **?? Backup Files...** | Backup specific files immediately (outside watched folders) |
| **? Pause** | Temporarily stop backups (e.g., during large file operations) |
| **? Stop** | Stop monitoring completely |

### Viewing Backup Progress

1. Go to the **Backup** tab to see:
   - Current backup status
   - List of backed-up files
   - Progress of ongoing operations

2. Go to the **Logs** tab for detailed activity history

---

## Restoring Files

The Restore tab lets you browse all files stored in your Azure Storage account and restore them to your computer.

### Step 1: Load Files from Azure

1. Go to the **Restore** tab
2. Make sure you're **unlocked** (if not, go to Settings and enter your password)
3. Click the **?? Load Files from Azure** button
4. Wait while the app retrieves the list of backed-up files
5. You'll see "X file(s)" showing how many files were found

> ?? **Note**: This reads metadata from Azure, so it may take a moment for large backups.

### Step 2: Find Your File

**Browse the list:**
- Files are sorted by most recently modified
- You can see the original file location, size, and modification date

**Search for specific files:**
1. Type a filename or part of a name in the search box
2. Click **?? Search** to filter the list

### Step 3: Choose Restore Destination

**Option A - Restore to original location:**
- Leave the destination empty
- The file will be restored to where it was originally backed up from

**Option B - Restore to a new location:**
1. Click **?? Browse...**
2. Select a folder where you want to save the restored files
3. Files will be saved to this folder

### Step 4: Restore Files

**Restore a single file:**
1. Click on a file in the list to select it
2. Click **?? Restore Selected**
3. Watch the progress bar

**Restore all files:**
1. Make sure you've selected a destination folder
2. Click **?? Restore All**
3. All files will be restored, recreating the original folder structure

### Restore Scenarios

| Scenario | Steps |
|----------|-------|
| **Recover a deleted file** | Load files ? Find the file ? Restore Selected |
| **Recover to a new computer** | Load files ? Browse destination ? Restore All |
| **Get an older version** | Load files ? Find file ? Restore to new location |
| **Verify backup works** | Load files ? Select any file ? Restore to temp folder |

### What Happens During Restore

1. The app downloads encrypted chunks from Azure
2. Chunks are decrypted using your password
3. The original file is reassembled
4. A hash check verifies the file wasn't corrupted
5. The file is saved to the destination

> ?? **Note**: You need the same password that was used to backup the files. Without it, restoration is impossible.

---

## Managing Watched Folders

### Add a New Folder

1. Go to **Settings**
2. Click **Add Folder**
3. Navigate to and select the folder
4. Configure exclusion patterns if needed
5. Click **Save Settings**
6. Run a **Full Scan** from the Dashboard to discover new files

### Remove a Folder

1. Go to **Settings**
2. Click on the folder in the list
3. Click **Remove**
4. Click **Save Settings**

> ?? **Note**: Removing a folder doesn't delete backed-up files from Azure. They remain available for restore.

### Temporarily Disable a Folder

1. Go to **Settings**
2. Uncheck the checkbox next to the folder
3. Click **Save Settings**

The folder won't be monitored until you re-enable it.

### Configure Exclusion Patterns

Exclusion patterns help you skip files that don't need backing up:

| Pattern | What it Excludes |
|---------|------------------|
| `*.tmp` | All .tmp files |
| `*.log` | All log files |
| `.git` | Git repository data |
| `node_modules` | npm dependencies |
| `bin;obj` | Build output folders |
| `.vs` | Visual Studio cache |
| `thumbs.db` | Windows thumbnail cache |
| `*.bak` | Backup files |

**Example**: `*.tmp;*.log;*.bak;node_modules;.git;bin;obj;.vs;thumbs.db`

---

## Understanding the Dashboard

### Statistics Cards

| Card | Description |
|------|-------------|
| **Total Files Backed Up** | Number of files currently backed up to Azure |
| **Total Size** | Combined size of all backed-up files |
| **Pending Changes** | Files waiting to be uploaded |
| **Last Backup** | When the last file was backed up |

### Status Bar (Bottom)

- **Left**: Current status message (last action performed)
- **Right**: Quick stats (file count and size)

### Header (Top Right)

- **Cost Estimate**: Estimated monthly cost based on current usage
- **Status Dot**: Green = monitoring, Gray = not monitoring

---

## Troubleshooting

### "Please initialize first"

You need to enter your password and click **Initialize** before using backup features.

### "Passwords do not match"

The password and confirmation don't match. Re-type both carefully.

### Files aren't being backed up

1. Check that the folder is **enabled** (checkbox checked)
2. Check that the file isn't matched by an **exclusion pattern**
3. Ensure you've run a **Full Scan** after adding folders
4. Verify **Monitoring** is started (green indicator)

### "Connection failed"

1. Verify your connection string is correct
2. Check your internet connection
3. Ensure the Azure container exists
4. Try **Test Connection** in Settings

### Restore fails with integrity error

The backed-up data may be corrupted. Try restoring an earlier version or contact support.

### Application closes unexpectedly

Check the **Logs** tab for error messages. Common causes:
- Network issues during upload/download
- Insufficient disk space
- File access permissions

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+S` | Save Settings (when in Settings tab) |

---

## Best Practices

1. **Run Full Scan weekly** to catch any missed files
2. **Test restore periodically** to verify backups are working
3. **Use exclusion patterns** to avoid backing up unnecessary files
4. **Monitor costs** in the header to stay within budget
5. **Keep your password safe** - consider a password manager
6. **Don't modify files during restore** to prevent conflicts
