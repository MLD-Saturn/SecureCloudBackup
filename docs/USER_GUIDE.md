# Azure Backup Tool - User Guide

This guide explains how to use the Azure Backup Tool for common backup and restore scenarios.

## Table of Contents

1. [First-Time Setup](#first-time-setup)
2. [Returning Users (Daily Unlock)](#returning-users-daily-unlock)
3. [The Sync View](#the-sync-view)
4. [Backing Up Files](#backing-up-files)
5. [Restoring Files](#restoring-files)
6. [Mirror Sync](#mirror-sync)
7. [Deleting Files from Azure](#deleting-files-from-azure)
8. [Managing Watched Folders](#managing-watched-folders)
9. [Storage Health](#storage-health)
10. [Settings](#settings)
11. [Logs](#logs)
12. [Drag and Drop](#drag-and-drop)
13. [Operation Previews](#operation-previews)
14. [Troubleshooting](#troubleshooting)
15. [Best Practices](#best-practices)

---

## First-Time Setup

When you first launch the app, you will see the Settings view with "Not Configured" status.

### Step 1: Choose an Authentication Method

In the **Authentication Method** card at the top of Settings, select one:

- **Connection String (Personal Accounts)** -- for personal Microsoft accounts. You will paste a connection string from the Azure Portal.
- **Microsoft Entra ID (Work/School)** -- for organizational accounts. Click **Sign in with Microsoft** to open a browser-based sign-in flow (you have 2 minutes to complete it). Then enter your **Storage Account Name** (just the name, not the full URL).

### Step 2: Configure Azure Storage

**If using Connection String:**

1. Paste your **Connection String** (found in Azure Portal > Storage Account > Access keys).
2. Enter a **Container Name** (default is "backup").
3. Click **Test Connection** to verify.

> **Tip**: Use the **Show** toggle next to the connection string field to reveal it.

**If using Entra ID:**

1. Enter your **Storage Account Name** (e.g., "mystorageaccount").
2. Enter a **Container Name**.
3. Click **Test Connection** to verify.

> **Note**: Your Azure account must have the **Storage Blob Data Contributor** role on the storage account.

### Step 3: Set Your Encryption Password

1. Enter a **strong password** in the password field.
2. **Confirm the password** by typing it again.
3. A red warning appears if the passwords do not match.

> **CRITICAL**: If you forget this password, your data **CANNOT** be recovered. There is no password reset. Store it safely!

### Step 4: Add Folders to Watch

1. Watched folders can be added from the **Sync** view after initializing (use the **+** button in the Local Files panel header).
2. Alternatively, folders appear in the Settings **Watched Folders** list once added.
3. Select a folder in the list to configure:
   - **Storage Tier** -- Hot (frequent access), Cool (infrequent, default), or Cold (rare access).
   - **Exclusion Patterns** -- semicolon-separated patterns to skip. Example: *.tmp;*.log;node_modules;.git;bin;obj;.vs

### Step 5: Initialize and Connect

Click **Initialize & Connect** to:
- Create and encrypt the local database with your password.
- Save and encrypt your connection string.
- Connect to Azure Storage.

You will see **[OK] Unlocked and Connected** when successful. The app automatically loads your Azure files and local watched folders.

---

## Returning Users (Daily Unlock)

When you relaunch the app after closing it, a **password dialog** appears automatically.

1. Enter your password and press **Enter** (or click **OK**).
2. The app unlocks, reconnects to Azure, and loads your files.

If you close the dialog, the app opens in locked state. You can unlock later from the **Settings** view by entering your password and clicking **Unlock**.

> **Note**: Your Azure connection and watched folders are already saved. You only need your password to unlock.
---

## The Sync View

The **Sync** view is the main workspace. It is divided into two side-by-side panels with a resizable splitter between them.

### Layout

| Area | Description |
|------|-------------|
| **Toolbar** (top) | Summary counts, primary action buttons, status indicator |
| **View Controls** (below toolbar) | Tree/list toggle, expand/collapse, search filter, selection controls |
| **Local Files** (left panel) | Files in your watched folders, with backup status per file |
| **Azure Backup** (right panel) | Files stored in Azure, with storage tier badges |
| **Progress Panel** (below, when active) | Overall and per-file progress bars, speed, ETA, cancel button |
| **Actions Panel** (below, when files selected) | Contextual actions for selected local and Azure files |

### Status Indicators

| Status | Indicator | Meaning |
|--------|-----------|---------|
| **Monitoring for Changes** | Green dot | Actively watching folders and backing up changes |
| **Ready (Not Monitoring)** | Gray dot | Unlocked but not watching for changes |
| **Locked (Enter Password)** | Gray dot | Needs password to unlock |
| **Not Configured** | Gray dot | First-time setup required |

### Toolbar Buttons

| Button | Description |
|--------|-------------|
| **Sync Selected** | Backup selected local files AND restore selected Azure files in one operation |
| **Start Monitoring** | Begin real-time file watching and automatic backup of changes |
| **Stop Monitoring** | Stop the file watcher |
| **Refresh** | Reload both local and Azure file lists |
| **Tree/List** | Toggle between folder tree view and flat list view |

### View Controls

- **Expand All / Collapse All** -- expand or collapse all tree nodes (visible in tree view mode).
- **Search** -- type a filename or path fragment to filter both panels. Click **X** to clear.
- **Select All / Deselect All** -- check or uncheck all files in both panels.

### Tree View vs. Flat List

- **Tree view** displays files organized in a folder hierarchy. Folders show aggregate status. You can expand, collapse, and right-click for context menus.
- **Flat list** shows every file in a single scrollable list with columns for path, size, and status.

Toggle between them using the **Tree/List** button in the toolbar.

---

## Backing Up Files

### Automatic Monitoring

1. Click **Start Monitoring** in the Sync toolbar.
2. The app watches all enabled folders for file changes.
3. Changed files are automatically queued and uploaded.
4. The status dot turns green while monitoring is active.

### Manual Backup of Selected Files

1. In the **Local Files** panel, check the files or folders you want to back up.
2. Click **Backup Selected** in the actions panel at the bottom.
3. A **Preview Dialog** appears showing what will be uploaded (new files, modified files, unchanged files).
4. Review the preview, uncheck any files you want to exclude, then click **Proceed**.
5. Watch the progress panel for overall and per-file progress.

### Context Menu (Tree View)

Right-click in the Local Files tree for additional options:

| Menu Item | Description |
|-----------|-------------|
| **Backup Selected** | Upload checked files to Azure |
| **Add Watched Folder...** | Add a new folder to the watch list |
| **Remove from Watch List** | Remove the selected folder |
| **Select All / Deselect All** | Bulk selection |
| **Expand All / Collapse All** | Tree navigation |
| **Refresh** | Reload local file list |
---

## Restoring Files

### Restore Selected Files

1. In the **Azure Backup** panel (right side), check the files you want to restore.
2. Choose a destination:
   - Check **Restore to original location** to put files back where they came from.
   - Or click **Browse...** to select a different destination folder.
3. Click **Restore Selected**.
4. A **Preview Dialog** shows what will be created or overwritten. Review and click **Proceed**.

### Path Remapping

When you select a folder in the Azure tree view, a **Remap path** panel appears above the file list. This lets you redirect an entire folder structure to a different location:

1. Select a folder node in the Azure tree.
2. In the remap panel, click **Browse...** or type a target path.
3. Click **Set** to apply.
4. Restored files under that folder will use the remapped path.
5. Click **Clear** to reset.

### Restore Scenarios

| Scenario | Steps |
|----------|-------|
| **Recover a deleted file** | Check the file in Azure panel > Restore Selected |
| **Recover to a new computer** | Check files > Browse destination > Restore Selected |
| **Restore a folder to a different location** | Select folder in tree > Remap path > Restore Selected |
| **Verify backup integrity** | Restore a file to a temp folder and compare |

### What Happens During Restore

1. Encrypted chunks are downloaded from Azure (in parallel).
2. Chunks are decrypted using your password.
3. The original file is reassembled.
4. A SHA-256 hash check verifies integrity.
5. The file is saved to the destination.

> **Note**: You need the same password that was used to back up the files. Without it, restoration is impossible.

---

## Mirror Sync

Mirror sync makes a local folder match the Azure backup exactly: missing files are restored, outdated files are updated, and extra local files are deleted.

1. In the Azure tree, select a folder node.
2. Use the **Remap path** panel to set the target local folder.
3. Click **Mirror Sync** (or right-click > Mirror Sync).
4. A **Preview Dialog** shows all planned actions (create, overwrite, delete, skip).
5. Review carefully -- deletions are permanent. Uncheck any items you want to exclude.
6. Click **Proceed** to start.

---

## Deleting Files from Azure

1. Check the files or folders you want to remove in the **Azure Backup** panel.
2. Click **Delete from Azure** in the actions panel (or right-click > Delete from Azure).
3. A **Preview Dialog** shows what will be deleted.
4. Review and click **Proceed**.

> **Warning**: Deleted files cannot be recovered from Azure after this operation.
---

## Managing Watched Folders

### Add a Folder

1. Go to the **Sync** view.
2. Click the **+** button in the Local Files panel header.
3. Browse to and select the folder.
4. The folder appears in the local tree immediately.

### Remove a Folder

1. Select the folder in the Local Files tree.
2. Click the **-** button in the panel header (or right-click > Remove from Watch List).

> **Note**: Removing a folder does not delete backed-up files from Azure. They remain available for restore.

### Configure a Folder

1. Go to **Settings**.
2. Click a folder in the **Watched Folders** list.
3. Adjust:
   - **Storage Tier** -- Hot, Cool (default), or Cold.
   - **Exclusion Patterns** -- semicolon-separated file/folder patterns to skip.

### Temporarily Disable a Folder

Uncheck the checkbox next to a folder in the Settings watched folders list. The folder will not be monitored until you re-enable it.

### Common Exclusion Patterns

| Pattern | What it Excludes |
|---------|------------------|
| *.tmp | Temporary files |
| *.log | Log files |
| .git | Git repository data |
| node_modules | npm dependencies |
| bin;obj | Build output folders |
| .vs | Visual Studio cache |
| thumbs.db | Windows thumbnail cache |
| *.bak | Backup files |

**Example**: *.tmp;*.log;*.bak;node_modules;.git;bin;obj;.vs;thumbs.db
---

## Storage Health

The **Storage Health** view provides visibility into your chunk-level storage and tools for maintenance.

### Chunk Index Summary

Displays three key metrics:

| Metric | Description |
|--------|-------------|
| **Total Chunks** | Number of content-addressed chunks stored in Azure |
| **Orphaned** | Chunks no longer referenced by any file (wasted storage) |
| **Deduplicated** | Chunks shared by multiple files (storage saved via deduplication) |

Also shows the timestamp of the last index rebuild and last Azure sync.

### Storage Tier Breakdown

Visual cards showing how many chunks and how much data is stored in each tier:

- **Hot** -- highest access speed, highest storage rate.
- **Cool** -- recommended for backups, lower storage rate.
- **Cold** -- lowest storage rate, highest retrieval latency.

### Orphan Detection and Cleanup

Orphaned chunks are blobs in Azure that no file references. They waste storage space.

1. Click **Scan for Orphans** to detect them.
2. Review the list showing hash, size, tier, upload date, and original file.
3. Use the **Select All** checkbox or check individual items.
4. Click **Delete Selected** to remove orphans from Azure.

### Index Management

| Action | Description |
|--------|-------------|
| **Backup to Azure** | Upload the chunk index to Azure for disaster recovery |
| **Restore from Azure** | Download the index from Azure (e.g., after reinstalling) |
| **Rebuild from Azure** | Rebuild the index by scanning all metadata blobs in Azure. Use this if the index is corrupted or out of sync. |
---

## Settings

The Settings view contains all configuration options.

### Authentication Method

Switch between **Connection String** and **Microsoft Entra ID** at any time.

### Connection String Section (Personal Accounts)

- **Connection String** -- paste or update your Azure connection string. It is encrypted before storage.
- **Container Name** -- the blob container to use (default: "backup").
- **Test Connection** -- verify connectivity without saving.
- **Update Connection String** -- clears the stored encrypted string so you can enter a new one. Your data and settings are preserved.
- **Save & Connect** -- encrypt and save the new connection string, then reconnect.

### Microsoft Entra ID Section (Work/School Accounts)

- **Sign in with Microsoft** -- opens browser-based sign-in (2-minute timeout). A cancel button appears during sign-in.
- **Storage Account Name** -- the Azure storage account name (not the full URL).
- **Container Name** -- the blob container to use.
- **Test Connection** -- verify connectivity.

### Password / Unlock

- **Returning users**: enter your password and click **Unlock**.
- **New users**: enter and confirm a password, then click **Initialize & Connect**.

### Watched Folders

Displays all watched folders with their enable/disable checkbox, storage tier badge, and exclusion patterns. Select a folder to configure its tier and exclusion patterns.

### Danger Zone

**Reset Application** securely deletes all local settings, credentials, and file tracking data. Your files in Azure Storage are not affected. Requires confirmation.

---

## Logs

The **Logs** view shows a chronological activity log in a monospaced font.

- **Diagnostic Logging** toggle (ON/OFF) -- when enabled, detailed service-level logs from encryption, chunking, restore, and blob services are included. Useful for troubleshooting.
- **Clear Logs** -- removes all log entries from the current session.
---

## Drag and Drop

The Sync view supports drag and drop between panels:

- **Drag files from the Azure panel to the Local panel** to restore them.
- **Drag files from the Local panel to the Azure panel** to back them up.

Drop targets highlight with a colored border and label when you drag over them.

---

## Operation Previews

Before any destructive or large operation (backup, restore, mirror sync, delete), a **Preview Dialog** appears showing:

- **Summary statistics** -- counts of files to create, overwrite, delete, or skip.
- **Transfer size** -- total bytes to upload or download.
- **File list** -- every affected file with its action, size, and reason.
- **Per-file checkboxes** -- uncheck files to exclude them from the operation.
- **Warning banners** -- appear for operations that will delete files.

Files are grouped by action type (new, overwrite, delete, skip) in collapsible sections. Click **Proceed** to execute or **Cancel** to abort.
---

## Troubleshooting

### "Please initialize first"

Enter your password in Settings and click **Unlock** or **Initialize & Connect**.

### "Passwords do not match"

The password and confirmation fields do not match. Re-type both carefully.

### "Invalid password"

The password does not match what was used during initial setup. Passwords are case-sensitive. There is no password recovery.

### Files are not being backed up

1. Ensure the folder is **enabled** (checkbox checked in Settings).
2. Ensure the file is not matched by an **exclusion pattern**.
3. Ensure **Monitoring** is started (green status dot in the Sync toolbar).
4. Try clicking **Refresh** in the Sync toolbar.

### "Connection failed"

1. Verify your connection string or storage account name is correct.
2. Check your internet connection.
3. Ensure the Azure storage account and container exist.
4. Try **Test Connection** in Settings.
5. For Entra ID, ensure you have the **Storage Blob Data Contributor** role.

### Restore fails with integrity error

The backed-up data may be corrupted. Check the Logs view for details. Try restoring a different file to confirm the issue is isolated.

### Application closes unexpectedly

Check the log file in the data directory for crash details. Common causes:
- Network interruption during upload/download
- Insufficient disk space
- File access permission errors

---

## Best Practices

1. **Run a Full Scan periodically** to catch any files missed by the real-time watcher.
2. **Test restore periodically** to verify backups are intact.
3. **Use exclusion patterns** to skip temporary files, build outputs, and caches.
4. **Keep your password safe** -- consider a password manager.
5. **Do not modify files during restore** to prevent conflicts.
6. **Use the Storage Health view** to detect and clean up orphaned chunks.
7. **Back up your chunk index to Azure** for disaster recovery.