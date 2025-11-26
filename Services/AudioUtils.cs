using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Barid.Fonix.AI.Whisper.Services;

public static class AudioUtils
{
    private const int TargetSampleRate = 16000;
    private const int TargetChannels = 1;

    public static float[] ConvertWavBytesToFloatSamples(byte[] wavBytes)
    {
        using var memoryStream = new MemoryStream(wavBytes);
        return ConvertWavStreamToFloatSamples(memoryStream);
    }

    public static float[] ConvertWavStreamToFloatSamples(Stream wavStream)
    {
        using var reader = new WaveFileReader(wavStream);
        return ConvertWaveProviderToFloatSamples(reader);
    }

    public static float[] ConvertWaveProviderToFloatSamples(IWaveProvider waveProvider)
    {
        var sampleProvider = ConvertToSampleProvider(waveProvider);

        if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
        }

        if (sampleProvider.WaveFormat.Channels != TargetChannels)
        {
            sampleProvider = sampleProvider.ToMono();
        }

        var samples = new List<float>();
        var buffer = new float[8192];
        int samplesRead;

        while ((samplesRead = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.Take(samplesRead));
        }

        return samples.ToArray();
    }

    public static bool ValidateWavHeader(byte[] wavBytes)
    {
        if (wavBytes.Length < 44)
        {
            return false;
        }

        var riff = System.Text.Encoding.ASCII.GetString(wavBytes, 0, 4);
        var wave = System.Text.Encoding.ASCII.GetString(wavBytes, 8, 4);

        return riff == "RIFF" && wave == "WAVE";
    }

    public static WaveFormat GetWaveFormat(byte[] wavBytes)
    {
        using var memoryStream = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(memoryStream);
        return reader.WaveFormat;
    }

    public static byte[] ConvertPcmToWav(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        var waveFormat = new WaveFormat(sampleRate, bitsPerSample, channels);

        using var memoryStream = new MemoryStream();
        using var writer = new WaveFileWriter(memoryStream, waveFormat);
        writer.Write(pcmData, 0, pcmData.Length);
        writer.Flush();

        return memoryStream.ToArray();
    }

    public static float[] ConvertInt16PcmToFloat(byte[] pcmData)
    {
        var samples = new float[pcmData.Length / 2];

        for (int i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(pcmData, i * 2);
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    private static ISampleProvider ConvertToSampleProvider(IWaveProvider waveProvider)
    {
        return waveProvider.WaveFormat.Encoding switch
        {
            WaveFormatEncoding.Pcm when waveProvider.WaveFormat.BitsPerSample == 8 =>
                new Pcm8BitToSampleProvider(waveProvider),
            WaveFormatEncoding.Pcm when waveProvider.WaveFormat.BitsPerSample == 16 =>
                new Pcm16BitToSampleProvider(waveProvider),
            WaveFormatEncoding.Pcm when waveProvider.WaveFormat.BitsPerSample == 24 =>
                new Pcm24BitToSampleProvider(waveProvider),
            WaveFormatEncoding.Pcm when waveProvider.WaveFormat.BitsPerSample == 32 =>
                new Pcm32BitToSampleProvider(waveProvider),
            WaveFormatEncoding.IeeeFloat =>
                new WaveToSampleProvider(waveProvider),
            _ => throw new ArgumentException($"Unsupported wave format: {waveProvider.WaveFormat.Encoding}")
        };
    }
}
