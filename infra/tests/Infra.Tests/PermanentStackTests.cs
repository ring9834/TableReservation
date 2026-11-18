using System.Text.Json;
using Amazon.CDK.Assertions;

namespace Reservation.Infra.Tests;

/// <summary>The Permanent stack (Design §17.4): private bucket, one CloudFront origin, headers, secrets kept.</summary>
public class PermanentStackTests
{
    // Managed policy IDs (AWS documentation).
    private const string CachingDisabled = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad";
    private const string CachingOptimized = "658327ea-f89d-4fab-a63d-7e88639e58f6";
    private const string AllViewerExceptHostHeader = "b689b0a8-53d0-40ab-baf2-68738e2966ac";

    [Fact]
    public void The_bucket_is_private_encrypted_ssl_only_and_kept()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        permanent.HasResource("AWS::S3::Bucket", new Dictionary<string, object>
        {
            ["DeletionPolicy"] = "Retain",
            ["Properties"] = new Dictionary<string, object>
            {
                ["PublicAccessBlockConfiguration"] = new Dictionary<string, object>
                {
                    ["BlockPublicAcls"] = true,
                    ["BlockPublicPolicy"] = true,
                    ["IgnorePublicAcls"] = true,
                    ["RestrictPublicBuckets"] = true,
                },
                ["BucketEncryption"] = Match.ObjectLike(new Dictionary<string, object>()),
            },
        });
        var policy = Synth.Single(permanent, "AWS::S3::BucketPolicy");
        var statements = policy.GetProperty("Properties").GetProperty("PolicyDocument").GetProperty("Statement").EnumerateArray().ToList();
        var deny = Assert.Single(statements, s => s.GetProperty("Effect").GetString() == "Deny");
        Assert.Contains("aws:SecureTransport", deny.ToString(), StringComparison.Ordinal);
        var allows = statements.Where(s => s.GetProperty("Effect").GetString() == "Allow").ToList();
        Assert.NotEmpty(allows);
        foreach (var allow in allows)
        {
            // Only CloudFront, and only for this distribution (OAC): nothing public.
            Assert.Equal("cloudfront.amazonaws.com", allow.GetProperty("Principal").GetProperty("Service").GetString());
            Assert.Contains("AWS:SourceArn", allow.GetProperty("Condition").ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_site_is_served_through_origin_access_control_with_the_index_function()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        permanent.ResourceCountIs("AWS::CloudFront::OriginAccessControl", 1);
        var config = Distribution(permanent);
        var s3Origin = config.GetProperty("Origins").EnumerateArray().Single(o => o.TryGetProperty("S3OriginConfig", out _));
        Assert.True(s3Origin.TryGetProperty("OriginAccessControlId", out _));
        var site = config.GetProperty("DefaultCacheBehavior");
        Assert.Equal(CachingOptimized, site.GetProperty("CachePolicyId").GetString());
        Assert.Equal("redirect-to-https", site.GetProperty("ViewerProtocolPolicy").GetString());
        var association = Assert.Single(site.GetProperty("FunctionAssociations").EnumerateArray());
        Assert.Equal("viewer-request", association.GetProperty("EventType").GetString());
        Assert.False(config.TryGetProperty("CustomErrorResponses", out _), "error pages would replace the API's Problem Details");
        Assert.Contains("index.html", Synth.Single(permanent, "AWS::CloudFront::Function").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Api_paths_go_to_lightsail_uncached_with_every_method_and_the_origin_header()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        var config = Distribution(permanent);
        var behaviors = config.GetProperty("CacheBehaviors").EnumerateArray().ToList();
        Assert.Equal(["/api/*", "/hubs/*", "/health/*"], behaviors.Select(b => b.GetProperty("PathPattern").GetString()));
        foreach (var behavior in behaviors)
        {
            Assert.Equal(CachingDisabled, behavior.GetProperty("CachePolicyId").GetString());
            Assert.Equal(AllViewerExceptHostHeader, behavior.GetProperty("OriginRequestPolicyId").GetString());
            Assert.Equal(7, behavior.GetProperty("AllowedMethods").GetArrayLength());
            Assert.Equal("https-only", behavior.GetProperty("ViewerProtocolPolicy").GetString());
        }

        var api = config.GetProperty("Origins").EnumerateArray().Single(o => o.GetProperty("DomainName").ValueKind == JsonValueKind.String
            && o.GetProperty("DomainName").GetString() == Synth.ApiHost);
        Assert.Equal("https-only", api.GetProperty("CustomOriginConfig").GetProperty("OriginProtocolPolicy").GetString());
        var header = Assert.Single(api.GetProperty("OriginCustomHeaders").EnumerateArray());
        Assert.Equal("X-Origin-Verify", header.GetProperty("HeaderName").GetString());
        Assert.Contains("{{resolve:secretsmanager:", header.GetProperty("HeaderValue").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_an_api_origin_the_distribution_serves_the_site_only()
    {
        var (permanent, _) = Synth.Templates(Synth.Full with { ApiOrigin = null });

        var config = Distribution(permanent);
        Assert.False(config.TryGetProperty("CacheBehaviors", out _));
        Assert.Single(config.GetProperty("Origins").EnumerateArray());
    }

    [Fact]
    public void Report_mode_sends_the_whole_policy_report_only_with_the_script_hashes()
    {
        var hashes = new[] { "'self'", "https://js.stripe.com", "'sha256-AAAA'" };
        var (permanent, _) = Synth.Templates(Synth.Full with { CspMode = CspMode.Report, CspScriptSources = hashes });

        var policy = HeadersPolicy(permanent);
        Assert.False(policy.GetProperty("SecurityHeadersConfig").TryGetProperty("ContentSecurityPolicy", out _));
        var custom = Assert.Single(policy.GetProperty("CustomHeadersConfig").GetProperty("Items").EnumerateArray());
        Assert.Equal("Content-Security-Policy-Report-Only", custom.GetProperty("Header").GetString());
        var csp = custom.GetProperty("Value").GetString()!;
        Assert.Contains("script-src 'self' https://js.stripe.com 'sha256-AAAA'", csp, StringComparison.Ordinal);
        AssertDirectives(csp);
    }

    [Fact]
    public void Enforce_mode_sends_the_policy_without_script_src_which_each_page_carries()
    {
        var (permanent, _) = Synth.Templates(Synth.Full with { CspMode = CspMode.Enforce });

        var policy = HeadersPolicy(permanent);
        Assert.False(policy.TryGetProperty("CustomHeadersConfig", out _));
        var csp = policy.GetProperty("SecurityHeadersConfig").GetProperty("ContentSecurityPolicy");
        Assert.True(csp.GetProperty("Override").GetBoolean());
        var value = csp.GetProperty("ContentSecurityPolicy").GetString()!;
        Assert.DoesNotContain("script-src", value, StringComparison.Ordinal);
        Assert.DoesNotContain("default-src", value, StringComparison.Ordinal);
        AssertDirectives(value);
    }

    [Fact]
    public void The_security_headers_are_hsts_nosniff_referrer_and_same_origin_frames()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        var security = HeadersPolicy(permanent).GetProperty("SecurityHeadersConfig");
        var hsts = security.GetProperty("StrictTransportSecurity");
        Assert.Equal(31_536_000, hsts.GetProperty("AccessControlMaxAgeSec").GetInt32());
        Assert.True(security.GetProperty("ContentTypeOptions").GetProperty("Override").GetBoolean());
        Assert.Equal("strict-origin-when-cross-origin", security.GetProperty("ReferrerPolicy").GetProperty("ReferrerPolicy").GetString());
        Assert.False(security.GetProperty("ReferrerPolicy").GetProperty("Override").GetBoolean()); // guest links keep no-referrer
        Assert.Equal("SAMEORIGIN", security.GetProperty("FrameOptions").GetProperty("FrameOption").GetString());
    }

    [Fact]
    public void A_policy_over_cloudfronts_limit_fails_the_synth()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"'sha256-{new string('A', 43)}{i:00}'").ToList();

        var error = Assert.Throws<InvalidOperationException>(() => ContentSecurityPolicy.Build(CspMode.Report, many));
        Assert.Contains("1783", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_are_generated_or_left_to_the_operator_and_kept()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        var secrets = Synth.Json(permanent).GetProperty("Resources").EnumerateObject()
            .Where(r => r.Value.GetProperty("Type").GetString() == "AWS::SecretsManager::Secret")
            .Select(r => r.Value)
            .ToList();
        Assert.Equal(
            ["reservation-demo/guest-links-signing-key", "reservation-demo/operator", "reservation-demo/origin-verify", "reservation-demo/pos-webhook-secret"],
            secrets.Select(s => s.GetProperty("Properties").GetProperty("Name").GetString()).Order());
        foreach (var secret in secrets)
        {
            Assert.Equal("Retain", secret.GetProperty("DeletionPolicy").GetString());
            var properties = secret.GetProperty("Properties");
            Assert.True(properties.TryGetProperty("GenerateSecretString", out _));
            Assert.False(properties.TryGetProperty("SecretString", out _), "no secret value in the template");
        }
    }

    [Fact]
    public void The_budget_alerts_at_80_and_100_percent_of_50_dollars()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        var budget = Synth.Single(permanent, "AWS::Budgets::Budget").GetProperty("Properties");
        Assert.Equal(50, budget.GetProperty("Budget").GetProperty("BudgetLimit").GetProperty("Amount").GetInt32());
        Assert.Equal([80, 100], budget.GetProperty("NotificationsWithSubscribers").EnumerateArray()
            .Select(n => n.GetProperty("Notification").GetProperty("Threshold").GetInt32()));
    }

    [Fact]
    public void The_site_domain_is_published_for_the_app_stack()
    {
        var (permanent, _) = Synth.Templates(Synth.Full);

        permanent.HasResourceProperties("AWS::SSM::Parameter", new Dictionary<string, object> { ["Name"] = "/reservation-demo/site-domain" });
    }

    [Theory]
    [InlineData("https://reservation-api.x.ap-southeast-2.cs.amazonlightsail.com/", "reservation-api.x.ap-southeast-2.cs.amazonlightsail.com")]
    [InlineData("reservation-api.x.ap-southeast-2.cs.amazonlightsail.com", "reservation-api.x.ap-southeast-2.cs.amazonlightsail.com")]
    public void The_api_origin_is_taken_as_a_host(string given, string expected) =>
        Assert.Equal(expected, DemoConfig.NormalizeHost(given));

    [Theory]
    [InlineData("http://x.example/")]
    [InlineData("x.example/api")]
    [InlineData("x.example:443")]
    public void Anything_but_a_host_is_refused(string given) =>
        Assert.Throws<ArgumentException>(() => DemoConfig.NormalizeHost(given));

    private static void AssertDirectives(string csp)
    {
        string[] expected =
        [
            "base-uri 'self'", "object-src 'none'", "frame-ancestors 'self'", "form-action 'self'", "img-src 'self' data:",
            "style-src 'self'", "font-src 'self'", "media-src 'self'", "connect-src 'self' https://api.stripe.com",
            "frame-src 'self' https://js.stripe.com https://hooks.stripe.com", "manifest-src 'self'", "worker-src 'none'",
        ];
        foreach (var directive in expected)
        {
            Assert.Contains(directive, csp, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
    }

    private static JsonElement Distribution(Template template) =>
        Synth.Single(template, "AWS::CloudFront::Distribution").GetProperty("Properties").GetProperty("DistributionConfig");

    private static JsonElement HeadersPolicy(Template template) =>
        Synth.Single(template, "AWS::CloudFront::ResponseHeadersPolicy").GetProperty("Properties").GetProperty("ResponseHeadersPolicyConfig");
}
