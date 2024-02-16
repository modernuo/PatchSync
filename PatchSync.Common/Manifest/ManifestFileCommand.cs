namespace PatchSync.Common.Manifest;

public enum ManifestFileCommand
{
    DeltaUpdate, // Default - download the signature file if available and diff to update, otherwise fall back to full download
    AlwaysFullUpdate, // Will download/install the full file, no matter what
    UpdateIfFullHashMismatch, // Full Update if hash doesn't match - No delta/rolling hash or signature file
    UpdateIfMissing, // Only download/install if missing
    NeverUpdate, // Only download/install the first time, never again, even if missing
    Delete // Deletes the file if it exists, otherwise nothing
}
