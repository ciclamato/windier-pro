using System.Net;
using System.Security.Cryptography;
using System.Text;
using Windows.System;

namespace PalmierPro.WinUI.AI;

public sealed record OAuthAuthorizationRequest(Uri AuthorizationUri, Uri RedirectUri, string State, string CodeVerifier);

/// <summary>
/// Generic OAuth 2.0 authorization-code helper for providers that document a desktop
/// integration. A website login is never treated as an API integration automatically.
/// </summary>
public sealed class OAuthPkceFlow
{
    public async Task<(OAuthAuthorizationRequest Request, Task<OAuthCallback> Callback)> StartAsync(
        Uri authorizationEndpoint,
        string clientId,
        IReadOnlyDictionary<string, string> scopes,
        CancellationToken cancellationToken = default)
    {
        var listener = new HttpListener();
        var port = FindFreePort();
        var redirect = new Uri($"http://127.0.0.1:{port}/oauth/callback/");
        listener.Prefixes.Add(redirect.ToString());
        listener.Start();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>(scopes, StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect.ToString(),
            ["response_type"] = "code",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var builder = new UriBuilder(authorizationEndpoint) { Query = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")) };
        var request = new OAuthAuthorizationRequest(builder.Uri, redirect, state, verifier);
        var callbackTask = WaitForCallbackAsync(listener, state, cancellationToken);
        if (!await Launcher.LaunchUriAsync(request.AuthorizationUri))
        {
            listener.Stop();
            throw new InvalidOperationException("Windows could not open the provider sign-in page.");
        }
        return (request, callbackTask);
    }

    private static async Task<OAuthCallback> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var query = context.Request.QueryString;
            var state = query["state"];
            var code = query["code"];
            var error = query["error"];
            var body = Encoding.UTF8.GetBytes(error is null ? "You can return to Palmier Pro." : "Sign-in was not completed.");
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken);
            context.Response.Close();
            if (!string.Equals(state, expectedState, StringComparison.Ordinal)) throw new InvalidOperationException("OAuth state validation failed.");
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException($"Provider sign-in failed: {error}");
            if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Provider sign-in did not return an authorization code.");
            return new OAuthCallback(code, state!);
        }
        finally { listener.Close(); }
    }

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record OAuthCallback(string Code, string State);
