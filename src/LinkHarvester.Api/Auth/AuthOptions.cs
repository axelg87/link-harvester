namespace LinkHarvester.Api.Auth;

public sealed class AuthOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "change-me";
    public int CookieDurationDays { get; set; } = 30;
}
