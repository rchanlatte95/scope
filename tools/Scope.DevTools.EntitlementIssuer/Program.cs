// DEV-ONLY offline entitlement-token issuance tool. See the header comment in
// Scope.DevTools.EntitlementIssuer.csproj for why this project is not shipped.
//
// Example:
//   dotnet run --project tools/Scope.DevTools.EntitlementIssuer -- \
//     --private-key-file C:/keys/dev-test-key.b64 \
//     --tier team --org-id acme --seat-id pilot-1 --expires-in-days 30

using Scope.DevTools.EntitlementIssuer;

return CliRunner.Run(args, Console.Out, Console.Error);
