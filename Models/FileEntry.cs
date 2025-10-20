using System; 
namespace FastDLX.Models;

public class FileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime? Modified { get; set; }
    public bool IsRemote { get; set; }
}
