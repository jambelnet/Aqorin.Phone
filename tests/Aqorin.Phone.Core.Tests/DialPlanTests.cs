using Aqorin.Phone.Core.Dialing;
using Aqorin.Phone.Core.Model;

namespace Aqorin.Phone.Core.Tests;

public class DialPlanTests
{
    private static readonly SipAccountSettings Default = new() { Registrar = "fritz.box", Username = "620" };

    [Theory]
    [InlineData("030 123456", "030123456")]
    [InlineData("030-123-456", "030123456")]
    [InlineData("(030) 123.456", "030123456")]
    [InlineData("030/123456", "030123456")]
    [InlineData("+49 30 123456", "004930123456")]
    [InlineData("  +4930123456  ", "004930123456")]
    [InlineData("**620", "**620")]
    [InlineData("*#06#", "*#06#")]
    [InlineData("0800 1 2 3", "0800123")]
    public void Numbers_are_normalised_and_addressed_to_the_registrar(string input, string expectedNumber)
    {
        var target = DialPlan.Normalize(input, Default);

        Assert.False(target.IsFullUri);
        Assert.Equal(expectedNumber, target.DisplayNumber);
        Assert.Equal($"sip:{expectedNumber}@fritz.box", target.SipUri);
    }

    [Fact]
    public void Non_default_port_and_tcp_transport_are_reflected_in_the_uri()
    {
        var settings = Default with { Port = 5080, Transport = SipTransport.Tcp };

        var target = DialPlan.Normalize("620", settings);

        Assert.Equal("sip:620@fritz.box:5080;transport=tcp", target.SipUri);
    }

    [Fact]
    public void Default_port_is_omitted()
    {
        var target = DialPlan.Normalize("620", Default with { Port = 5060 });
        Assert.Equal("sip:620@fritz.box", target.SipUri);
    }

    [Fact]
    public void Ip_address_registrar_is_used_verbatim_but_never_hard_coded()
    {
        var target = DialPlan.Normalize("620", Default with { Registrar = "192.168.178.1" });
        Assert.Equal("sip:620@192.168.178.1", target.SipUri);
    }

    [Theory]
    [InlineData("sip:620@fritz.box", "sip:620@fritz.box", "620")]
    [InlineData("SIP:0301234@192.168.178.1:5060", "SIP:0301234@192.168.178.1:5060", "0301234")]
    [InlineData("sips:alice@example.com", "sips:alice@example.com", "alice")]
    [InlineData("620@fritz.box", "sip:620@fritz.box", "620")]
    public void Full_sip_addresses_pass_through(string input, string expectedUri, string expectedDisplay)
    {
        var target = DialPlan.Normalize(input, Default);

        Assert.True(target.IsFullUri);
        Assert.Equal(expectedUri, target.SipUri);
        Assert.Equal(expectedDisplay, target.DisplayNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("12a34")]
    [InlineData("sip:")]
    [InlineData("sip:@fritz.box")]
    [InlineData("@fritz.box")]
    [InlineData("620@")]
    [InlineData("sip:6 20@fritz.box")]
    [InlineData("+")]
    [InlineData("- - -")]
    public void Invalid_destinations_are_rejected_with_a_friendly_error(string? input)
    {
        Assert.False(DialPlan.TryNormalize(input, Default, out var target, out var error));
        Assert.Null(target);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<InvalidDestinationException>(() => DialPlan.Normalize(input, Default));
    }

    [Fact]
    public void Plus_in_the_middle_is_treated_as_noise()
    {
        Assert.Equal("0049123", DialPlan.NormalizeNumber("+49+123"));
    }

    [Theory]
    [InlineData("sip:620@fritz.box", "620")]
    [InlineData("sip:620@fritz.box;transport=tcp", "620")]
    [InlineData("sips:bob@host", "bob")]
    [InlineData("bob@host", "bob")]
    [InlineData("bob", "bob")]
    public void UserPart_extracts_the_user(string uri, string expected)
    {
        Assert.Equal(expected, DialPlan.UserPart(uri));
    }
}
