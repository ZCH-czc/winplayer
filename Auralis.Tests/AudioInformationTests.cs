using System.Buffers.Binary;
using Auralis.Services;

internal static class AudioInformationTests
{
    internal static void Run()
    {
        // One second, 96kHz/24-bit/stereo; exactly 100000 encoded audio bytes.
        // A large picture/padding block must not inflate the encoded bitrate.
        foreach (var padding in new[] {0, 1000000})
        {
            var bytes = new byte[42 + 4 + padding + 100000];
            "fLaC"u8.CopyTo(bytes); bytes[7] = 34;
            ulong info = (96000UL << 44) | (1UL << 41) | (23UL << 36) | 96000;
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18,8), info);
            bytes[42] = 129; bytes[43]=(byte)(padding>>16);bytes[44]=(byte)(padding>>8);bytes[45]=(byte)padding;
            using var stream=new MemoryStream(bytes);
            var result=FlacAudioInformation.Read(stream);
            if(result is not {BitrateKbps:800,SampleRateHz:96000,BitsPerSample:24,Channels:2,IsLossless:true,IsAverageBitrate:true})
                throw new Exception("FLAC encoded average must exclude metadata and not use PCM throughput.");
            bytes[43]=255;bytes[44]=255;bytes[45]=255;stream.Position=0;
            if(FlacAudioInformation.Read(stream) is not null) throw new Exception("Truncated metadata accepted.");
            bytes[0]=0;stream.Position=0;
            if(FlacAudioInformation.Read(stream) is not null) throw new Exception("Non-FLAC accepted.");
        }
        Console.WriteLine("PASS audio information: FLAC sample format, encoded average, metadata exclusion and invalid input.");
    }
}
