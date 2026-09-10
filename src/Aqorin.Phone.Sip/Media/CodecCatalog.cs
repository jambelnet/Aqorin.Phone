using Aqorin.Phone.Core.Abstractions;
using SIPSorceryMedia.Abstractions;

namespace Aqorin.Phone.Sip.Media;

/// <summary>Maps the application's <see cref="AudioCodec"/> preference list to SIPSorcery <see cref="AudioFormat"/>s.</summary>
internal static class CodecCatalog
{
    public static List<AudioFormat> Formats(IReadOnlyList<AudioCodec> preference)
    {
        var list = new List<AudioFormat>();
        foreach (var codec in preference)
        {
            var format = codec switch
            {
                AudioCodec.Pcma => new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
                AudioCodec.Pcmu => new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                _ => AudioFormat.Empty,
            };
            if (!format.IsEmpty() && !list.Contains(format))
            {
                list.Add(format);
            }
        }

        if (list.Count == 0)
        {
            list.Add(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA));
            list.Add(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
        }

        return list;
    }

    public static AudioSamplingRatesEnum SamplingRate(int clockRate) => clockRate switch
    {
        16000 => AudioSamplingRatesEnum.Rate16KHz,
        24000 => AudioSamplingRatesEnum.Rate24kHz,
        44100 => AudioSamplingRatesEnum.Rate44_1kHz,
        48000 => AudioSamplingRatesEnum.Rate48kHz,
        _ => AudioSamplingRatesEnum.Rate8KHz,
    };
}
