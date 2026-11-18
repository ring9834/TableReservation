using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SecretsManager;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace Reservation.Infra;

/// <summary>
/// What survives deleting the app (Design §17.4): the public <c>*.cloudfront.net</c> address, the site bucket, the
/// secrets and the budget. The distribution serves the static export from S3 and, once <see cref="DemoConfig.ApiOrigin"/>
/// is known, proxies <c>/api/*</c>, <c>/hubs/*</c> and <c>/health/*</c> to the Lightsail container on the same origin,
/// so the SameSite=Strict session cookie works and no CORS is needed.
/// </summary>
public sealed class PermanentStack : Stack
{
    public const string SecretPrefix = "reservation-demo/";
    public const string OperatorSecretName = SecretPrefix + "operator";
    public const string GuestLinkKeyName = SecretPrefix + "guest-links-signing-key";
    public const string PosWebhookSecretName = SecretPrefix + "pos-webhook-secret";
    public const string OriginVerifyName = SecretPrefix + "origin-verify";
    public const string SiteDomainParameter = "/reservation-demo/site-domain";
    public const string OriginHeader = "X-Origin-Verify";

    /// <summary>The values only the operator knows; the RUNBOOK puts them in with put-secret-value.</summary>
    public static readonly string[] OperatorKeys =
        ["HostPasscode", "PresenterPasscode", "StripeSecretKey", "StripePublishableKey", "StripeWebhookSecret"];

    /// <summary>The API routes on the distribution (the rest is the static site).</summary>
    public static readonly string[] ApiPaths = ["/api/*", "/hubs/*", "/health/*"];

    public PermanentStack(Construct scope, string id, IStackProps props, DemoConfig config)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(config);

        var bucket = new Bucket(this, "Site", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            ObjectOwnership = ObjectOwnership.BUCKET_OWNER_ENFORCED,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

        // Machine-only secrets: generated in AWS, never seen by anyone.
        var originVerify = GeneratedSecret("OriginVerify", OriginVerifyName, "CloudFront → API origin header (Design §17.4)");
        GeneratedSecret("GuestLinkKey", GuestLinkKeyName, "Guest link signing key (Design §7.3)");
        GeneratedSecret("PosWebhookSecret", PosWebhookSecretName, "Mock POS BillClosed signing secret (Design §6.7)");

        // Operator secret: created once with empty keys; the operator's put-secret-value is never overwritten, because
        // the template never changes the value (a generated string is only generated on create).
        _ = new Secret(this, "Operator", new SecretProps
        {
            SecretName = OperatorSecretName,
            Description = "Passcodes and Stripe test-mode keys for the AWS demo; values put in by the operator (RUNBOOK)",
            GenerateSecretString = new SecretStringGenerator
            {
                SecretStringTemplate = JsonSerializer.Serialize(OperatorKeys.ToDictionary(k => k, _ => string.Empty)),
                GenerateStringKey = "Placeholder",
                ExcludePunctuation = true,
            },
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

        var headers = SecurityHeaders(config);
        var siteBehavior = new BehaviorOptions
        {
            Origin = S3BucketOrigin.WithOriginAccessControl(bucket),
            ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
            CachePolicy = CachePolicy.CACHING_OPTIMIZED,
            ResponseHeadersPolicy = headers,
            Compress = true,
            FunctionAssociations =
            [
                new FunctionAssociation
                {
                    EventType = FunctionEventType.VIEWER_REQUEST,
                    Function = new Function(this, "TrailingSlashIndex", new FunctionProps
                    {
                        Runtime = FunctionRuntime.JS_2_0,
                        Comment = "Static export with trailingSlash: /host/ → /host/index.html; /host → 301 /host/",
                        Code = FunctionCode.FromInline(SiteFunction.Code),
                    }),
                },
            ],
        };

        // No custom error responses: they apply to every behaviour, so they would replace the API's Problem Details
        // (404 not_found, 403 …) with an HTML page.
        var distribution = new Distribution(this, "Distribution", new DistributionProps
        {
            Comment = "Reservation demo: static site + API on one origin",
            DefaultBehavior = siteBehavior,
            AdditionalBehaviors = config.ApiOrigin is { } apiOrigin ? ApiBehaviors(apiOrigin, originVerify, headers) : null,
            DefaultRootObject = "index.html",
            HttpVersion = HttpVersion.HTTP2_AND_3,
            PriceClass = PriceClass.PRICE_CLASS_ALL, // edge locations in New Zealand
            EnableIpv6 = true,
        });

        // For the App stack (GuestLinks:BaseUrl), read at its deploy time; no cross-stack export to lock this stack.
        _ = new StringParameter(this, "SiteDomain", new StringParameterProps
        {
            ParameterName = SiteDomainParameter,
            StringValue = distribution.DistributionDomainName,
            Description = "The demo's public host (CloudFront)",
        });

        if (config.BudgetEmail is { } email)
        {
            _ = Budget(email);
        }
        else
        {
            Annotations.Of(this).AddWarning("No -c BudgetEmail=…: the US$50 budget alert is not in this template (a deploy would remove it).");
        }

        if (config.CspMode == CspMode.Report && config.CspScriptSources.Count == 0)
        {
            Annotations.Of(this).AddWarning("CspMode=report without -c CspScriptHashesFile=…: every inline script will be reported.");
        }

        _ = new CfnOutput(this, "SiteUrl", new CfnOutputProps { Value = "https://" + distribution.DistributionDomainName });
        _ = new CfnOutput(this, "DistributionId", new CfnOutputProps { Value = distribution.DistributionId });
        _ = new CfnOutput(this, "BucketName", new CfnOutputProps { Value = bucket.BucketName });
        _ = new CfnOutput(this, "ApiOrigin", new CfnOutputProps { Value = config.ApiOrigin ?? "(none: deploy with -c ApiOrigin=<host>)" });
        _ = new CfnOutput(this, "CspMode", new CfnOutputProps { Value = config.CspMode.ToString().ToLowerInvariant() });
    }

    private Secret GeneratedSecret(string id, string name, string description) => new(this, id, new SecretProps
    {
        SecretName = name,
        Description = description,
        GenerateSecretString = new SecretStringGenerator { PasswordLength = 64, ExcludePunctuation = true },
        RemovalPolicy = RemovalPolicy.RETAIN,
    });

    private ResponseHeadersPolicy SecurityHeaders(DemoConfig config)
    {
        var csp = ContentSecurityPolicy.Build(config.CspMode, config.CspScriptSources);
        var enforce = config.CspMode == CspMode.Enforce;
        return new ResponseHeadersPolicy(this, "SecurityHeaders", new ResponseHeadersPolicyProps
        {
            Comment = $"Reservation demo security headers (CSP {config.CspMode.ToString().ToLowerInvariant()})",
            SecurityHeadersBehavior = new ResponseSecurityHeadersBehavior
            {
                StrictTransportSecurity = new ResponseHeadersStrictTransportSecurity
                {
                    AccessControlMaxAge = Duration.Days(365),
                    IncludeSubdomains = true,
                    Override = true,
                },
                ContentTypeOptions = new ResponseHeadersContentTypeOptions { Override = true },
                // Not overriding: the API's guest-link responses say no-referrer (Design §7.3) and keep it.
                ReferrerPolicy = new ResponseHeadersReferrerPolicy
                {
                    ReferrerPolicy = HeadersReferrerPolicy.STRICT_ORIGIN_WHEN_CROSS_ORIGIN,
                    Override = false,
                },
                FrameOptions = new ResponseHeadersFrameOptions { FrameOption = HeadersFrameOption.SAMEORIGIN, Override = true },
                ContentSecurityPolicy = enforce
                    ? new ResponseHeadersContentSecurityPolicy { ContentSecurityPolicy = csp, Override = true }
                    : null,
            },
            CustomHeadersBehavior = enforce
                ? null
                : new ResponseCustomHeadersBehavior
                {
                    CustomHeaders = [new ResponseCustomHeader { Header = ContentSecurityPolicy.ReportOnlyHeader, Value = csp, Override = true }],
                },
        });
    }

    private static Dictionary<string, IBehaviorOptions> ApiBehaviors(string apiOrigin, Secret originVerify, IResponseHeadersPolicy headers)
    {
        var origin = new HttpOrigin(apiOrigin, new HttpOriginProps
        {
            ProtocolPolicy = OriginProtocolPolicy.HTTPS_ONLY,
            OriginSslProtocols = [OriginSslPolicy.TLS_V1_2],
            ReadTimeout = Duration.Seconds(30),
            // Resolved by CloudFormation from Secrets Manager; the API refuses requests without it (Design §17.4).
            CustomHeaders = new Dictionary<string, string> { [OriginHeader] = originVerify.SecretValue.UnsafeUnwrap() },
        });

        // No caching, every method, and every header (except Host, which must name the Lightsail endpoint), cookie and
        // query string forwarded; the managed policy also forwards CloudFront-Viewer-Address. WebSockets (SignalR) pass
        // through CloudFront as they are.
        var behavior = new BehaviorOptions
        {
            Origin = origin,
            ViewerProtocolPolicy = ViewerProtocolPolicy.HTTPS_ONLY,
            AllowedMethods = AllowedMethods.ALLOW_ALL,
            CachePolicy = CachePolicy.CACHING_DISABLED,
            OriginRequestPolicy = OriginRequestPolicy.ALL_VIEWER_EXCEPT_HOST_HEADER,
            ResponseHeadersPolicy = headers,
            Compress = false,
        };
        return ApiPaths.ToDictionary(path => path, IBehaviorOptions (_) => behavior);
    }

    private CfnBudget Budget(string email) => new(this, "MonthlyBudget", new CfnBudgetProps
    {
        Budget = new CfnBudget.BudgetDataProperty
        {
            BudgetName = "reservation-demo-monthly",
            BudgetType = "COST",
            TimeUnit = "MONTHLY",
            BudgetLimit = new CfnBudget.SpendProperty { Amount = 50, Unit = "USD" },
        },
        NotificationsWithSubscribers = new[] { 80, 100 }.Select(threshold => new CfnBudget.NotificationWithSubscribersProperty
        {
            Notification = new CfnBudget.NotificationProperty
            {
                NotificationType = "ACTUAL",
                ComparisonOperator = "GREATER_THAN",
                Threshold = threshold,
                ThresholdType = "PERCENTAGE",
            },
            Subscribers = new[] { new CfnBudget.SubscriberProperty { SubscriptionType = "EMAIL", Address = email } },
        }).ToArray(),
    });
}
