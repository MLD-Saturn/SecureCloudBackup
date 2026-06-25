using System;
using System.IO;

namespace SecureCloudBackup;

/// <summary>
/// Determines whether the application is running in portable or installed mode.
/// Portable mode stores data alongside the executable.
/// Installed mode stores data in the user's LocalAppData folder.
/// </summary>
public static class AppMode
{
    private const string PortableMarkerFileName = "portable.marker";
    
    private static bool? _isPortable;
    
    /// <summary>
    /// Gets whether the application is running in portable mode.
    /// Portable mode is detected by the presence of a "portable.marker" file
    /// in the same directory as the executable.
    /// </summary>
    public static bool IsPortable
    {
        get
        {
            _isPortable ??= File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName));
            return _isPortable.Value;
        }
    }
    
    /// <summary>
    /// Gets the display name for the current mode.
    /// </summary>
    public static string ModeName => IsPortable ? "Portable" : "Installed";
    
    /// <summary>
    /// Gets the appropriate data directory based on the current mode.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            if (IsPortable)
            {
                // Portable: store data alongside the executable
                return AppContext.BaseDirectory;
            }
            else
            {
                // Installed: store data in user's LocalAppData
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SecureCloudBackup");
                
                // Ensure directory exists
                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }
                
                return appDataPath;
            }
        }
    }
    
    /// <summary>
    /// Gets the full path to the database file.
    /// </summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "backup.db");
    
    /// <summary>
    /// Gets the window title suffix indicating the mode.
    /// </summary>
    public static string WindowTitleSuffix => IsPortable ? " (Portable)" : "";
}
