// AgentCoreProcessor.Tests/IcalinguaProtocolTests.cs
using System;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Adapter;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class IcalinguaProtocolTests
{
    // 32 字节测试 seed：全 0x01
    private static byte[] Seed0101()
    {
        var seed = new byte[32];
        for (int i = 0; i < 32; i++) seed[i] = 0x01;
        return seed;
    }

    // ── ParseSeed：三种编码格式 → 相同 32 字节 ──

    [Fact]
    public void ParseSeed_Base64()
    {
        var b64 = Convert.ToBase64String(Seed0101());
        var seed = IcalinguaProtocol.ParseSeed(b64);
        Assert.NotNull(seed);
        Assert.Equal(Seed0101(), seed!);
    }

    [Fact]
    public void ParseSeed_Hex()
    {
        var hex = Convert.ToHexString(Seed0101()).ToLowerInvariant();
        var seed = IcalinguaProtocol.ParseSeed(hex);
        Assert.NotNull(seed);
        Assert.Equal(Seed0101(), seed!);
    }

    [Fact]
    public void ParseSeed_Raw32Bytes()
    {
        var raw = new string('A', 32); // 32 ASCII 字符 = 32 字节
        var seed = IcalinguaProtocol.ParseSeed(raw);
        Assert.NotNull(seed);
        Assert.Equal(32, seed!.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(raw), seed);
    }

    [Fact]
    public void ParseSeed_Invalid_ReturnsNull()
    {
        Assert.Null(IcalinguaProtocol.ParseSeed(""));
        Assert.Null(IcalinguaProtocol.ParseSeed("too-short"));
    }

    // ── SignSalt：RFC8032 标准 ed25519 签名，64 字节、可验证 ──

    [Fact]
    public void SignSalt_ProducesVerifiableSignature()
    {
        var seed = Seed0101();
        var salt = "some-salt-string-123";
        var sig = IcalinguaProtocol.SignSalt(salt, seed);

        Assert.NotNull(sig);
        Assert.Equal(64, sig!.Length);

        // 用 BouncyCastle 独立验签，确认是标准 ed25519
        var key = new Ed25519PrivateKeyParameters(seed, 0);
        var verifier = new Ed25519Signer();
        verifier.Init(false, key.GeneratePublicKey());
        var data = Encoding.UTF8.GetBytes(salt);
        verifier.BlockUpdate(data, 0, data.Length);
        Assert.True(verifier.VerifySignature(sig));
    }

    [Fact]
    public void SignSalt_DifferentSalt_DifferentSignature()
    {
        var seed = Seed0101();
        var s1 = IcalinguaProtocol.SignSalt("salt-a", seed);
        var s2 = IcalinguaProtocol.SignSalt("salt-b", seed);
        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.False(s1!.SequenceEqual(s2!));
    }

    // ── roomId 映射（§9.1）：私聊=对方uin(正)，群聊=-group_id ──

    [Theory]
    [InlineData(10001L, "private_10001", true)]
    [InlineData(-616565017L, "group_616565017", false)]
    public void RoomToChannel(long roomId, string expectedChannel, bool expectedPrivate)
    {
        var (channel, isPrivate) = IcalinguaProtocol.RoomToChannel(roomId);
        Assert.Equal(expectedChannel, channel);
        Assert.Equal(expectedPrivate, isPrivate);
    }

    [Theory]
    [InlineData("private_10001", "10001")]
    [InlineData("group_616565017", "-616565017")]
    public void ChannelToRoomId(string channel, string expected)
    {
        Assert.Equal(expected, IcalinguaProtocol.ChannelToRoomId(channel));
    }
}
