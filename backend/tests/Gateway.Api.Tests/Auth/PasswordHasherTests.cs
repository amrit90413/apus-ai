using Gateway.Api.Auth;

namespace Gateway.Api.Tests.Auth;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_then_Verify_round_trips()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", stored));
    }

    [Fact]
    public void Verify_rejects_the_wrong_password()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(PasswordHasher.Verify("correct horse battery stapl", stored));
        Assert.False(PasswordHasher.Verify("", stored));
    }

    [Fact]
    public void Hash_salts_so_the_same_password_hashes_differently()
    {
        var first = PasswordHasher.Hash("p@ssw0rd-long-enough");
        var second = PasswordHasher.Hash("p@ssw0rd-long-enough");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("p@ssw0rd-long-enough", first));
        Assert.True(PasswordHasher.Verify("p@ssw0rd-long-enough", second));
    }

    [Fact]
    public void Hash_uses_the_iterations_salt_hash_format()
    {
        var parts = PasswordHasher.Hash("unicode-пароль-密码").Split('.');

        Assert.Equal(3, parts.Length);
        Assert.Equal("600000", parts[0]);
        Assert.Equal(16, Convert.FromBase64String(parts[1]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[2]).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("600000.c2FsdA==")]
    [InlineData("600000.c2FsdA==.aGFzaA==.extra")]
    public void Verify_returns_false_for_a_stored_value_with_the_wrong_number_of_parts(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }
}
