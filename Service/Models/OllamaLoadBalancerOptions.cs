namespace MEAIGPTAPI.Models
{
    public class OllamaLoadBalancerOptions
    {
        public List<string> Endpoints { get; set; } = new List<string>();
        public int HealthCheckIntervalSeconds { get; set; } = 30;
        public int TimeoutMinutes { get; set; } = 10;
    }
}
