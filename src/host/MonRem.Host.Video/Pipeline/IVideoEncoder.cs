namespace MonRem.Host.Video.Pipeline;

public interface IVideoEncoder : IDisposable
{
    /// <summary>Um access unit H.264 Annex B completo (com AUD no início).</summary>
    ReadOnlyMemory<byte> Encode(in Frame frame, bool forceKeyframe);
}
