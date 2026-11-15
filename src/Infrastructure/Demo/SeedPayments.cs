namespace Reservation.Infrastructure.Demo;

/// <summary>
/// The payment intents the demo seed gives its captured deposits (Design §17, §7.3). Stripe has never seen them, so in
/// Stripe mode <see cref="Payments.SeedDepositRouter"/> refunds them through the simulated path instead.
/// </summary>
public static class SeedPayments
{
    public const string IntentPrefix = "pi_seed_";

    public static string IntentFor(string reference) => IntentPrefix + reference;

    public static bool IsSeeded(string paymentIntentId) =>
        paymentIntentId.StartsWith(IntentPrefix, StringComparison.Ordinal);
}
