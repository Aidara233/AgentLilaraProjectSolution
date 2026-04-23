using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class WebAuthService
    {
        private readonly WebConfig config;

        public WebAuthService(WebConfig config)
        {
            this.config = config;
        }

        public bool ValidateCredentials(string username, string password)
        {
            var hash = WebConfig.HashPassword(password);
            return config.Admins.Any(a =>
                a.Username.Equals(username, System.StringComparison.OrdinalIgnoreCase)
                && a.PasswordHash == hash);
        }

        public ClaimsPrincipal CreatePrincipal(string username)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            return new ClaimsPrincipal(identity);
        }
    }
}
