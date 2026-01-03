using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.DirectoryServices.AccountManagement;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class LoginController : ControllerBase
    {
        private readonly JwtSettings _jwtSettings;
        private readonly IConfiguration _configuration;

        public LoginController(IOptions<JwtSettings> jwtSettings, IConfiguration configuration)
        {
            _jwtSettings = jwtSettings.Value;
            _configuration = configuration;
        }

        [HttpPost]
        public IActionResult Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
                return BadRequest("Username and Password must be provided.");

            string domain = _configuration["ADSettings:Domain"];

            using (var context = new PrincipalContext(ContextType.Domain, domain))
            {
                bool isValid = context.ValidateCredentials(request.Username, request.Password);
                if (!isValid)
                    return Unauthorized("Invalid username or password.");

                var userPrincipal = UserPrincipal.FindByIdentity(context, request.Username);
                if (userPrincipal == null)
                    return Unauthorized("User not found in AD.");

                var groups = userPrincipal.GetAuthorizationGroups()
                                          .Select(g => g.SamAccountName)
                                          .Where(name => !string.IsNullOrEmpty(name))
                                          .ToList();

                // Determine plant access based on AD groups
                string plantAccess = DeterminePlantAccess(groups);

                var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("PlantAccess", plantAccess) // Add plant access claim
        };

                foreach (var group in groups)
                {
                    claims.Add(new Claim(ClaimTypes.Role, group));
                }

                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
                var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

                var token = new JwtSecurityToken(
                    issuer: _jwtSettings.Issuer,
                    audience: _jwtSettings.Audience,
                    claims: claims,
                    expires: DateTime.Now.AddHours(_jwtSettings.ExpiryInHours),
                    signingCredentials: creds
                );

                var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

                return Ok(new
                {
                    Token = tokenString,
                    Username = request.Username,
                    DisplayName = userPrincipal.DisplayName ?? request.Username,
                    Groups = groups,
                    PlantAccess = plantAccess
                });
            }
        }

        private string DeterminePlantAccess(List<string> groups)
        {
            bool hasManesar = groups.Any(g => g.Contains("Manesar", StringComparison.OrdinalIgnoreCase));
            bool hasSanand = groups.Any(g => g.Contains("Sanand", StringComparison.OrdinalIgnoreCase));

            if (hasManesar && hasSanand)
                return "Both";
            else if (hasManesar)
                return "Manesar";
            else if (hasSanand)
                return "Sanand";
            else
                return "Manesar"; // Default
        }

        public class LoginRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }
        public class JwtSettings
        {
            public string SecretKey { get; set; }
            public string Issuer { get; set; }
            public string Audience { get; set; }
            public int ExpiryInHours { get; set; }
        }
    }
}
