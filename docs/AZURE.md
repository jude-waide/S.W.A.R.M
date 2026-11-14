# Azure App Service

The dashboard and API deploy together to a **single Linux App Service instance**.
Mission state and SignalR subscriptions are in memory: restarting loses the
mission, and scaling to multiple instances is not supported.

## Demo deployment

- Website: <https://drone-swarm-durhack2026.azurewebsites.net>
- Subscription: Visual Studio Professional Subscription
  (`bb27d120-70cc-4e48-980c-bc656731bd49`). Do not deploy to a work subscription.
- Resource group: `rg-drone-swarm-demo`.
- App: `drone-swarm-durhack2026`; plan: `asp-drone-swarm-demo`, Linux B1,
  one instance in UK West. UK South had no B1 quota.
- The subscription spending limit was verified as **On** before creation;
  leave it enabled to retain the credit-only protection.
- The plugin secret is stored in the app's environment variables as `PLUGIN_KEY`.
  Retrieve it through the Azure portal when configuring the Minecraft plugin;
  do not paste it into chat or source control.

## Publish

From the repository root, with Node.js 22.12+ and the .NET 10 SDK installed:

```powershell
.\scripts\publish-azure.ps1
```

The script builds the dashboard, publishes a self-contained `linux-x64` app,
and prints the ZIP path under the ignored `artifacts\azure` directory. No
plugin key or local environment file is included. Each run uses a fresh output
directory to avoid deploying stale files.
Restore uses the public NuGet feed explicitly and build intermediates are
isolated, so unrelated user-level feeds or a running local app do not interfere.

## Create and configure

In the Azure portal, select the subscription that holds your credit, then create
a Web App with:

- Publish: Code; operating system: Linux; runtime stack: .NET 10.
- One Basic B1 App Service plan, with zone redundancy disabled. Confirm the
  current regional price before creation. Credit is not a spending cap.
- No database, Application Insights, or other paid add-ons.
- HTTPS Only enabled, minimum TLS 1.2, FTPS disabled, Always On enabled.
- Startup command: `dotnet DroneSwarm.Web.dll`. The self-contained publish
  includes its own runtime; do not enable a remote source build.
- Environment variables: `ASPNETCORE_URLS=http://0.0.0.0:8080`,
  `ASPNETCORE_ENVIRONMENT=Production`, and `PLUGIN_KEY` set to a new random
  secret. Keep this secret out of source control and deployment archives.

Linux App Service supports WebSockets for SignalR. Keep the instance count at
one; the existing same-origin frontend needs no API URL or CORS changes.

## Deploy and verify

Using an organisation-approved Azure CLI sign-in, explicitly target the intended
subscription for every command:

```powershell
az webapp deploy --subscription <subscription-id> --resource-group <resource-group> `
  --name <app-name> --src-path <printed-zip-path> --type zip `
  --enable-kudu-warmup false --async true --timeout 600000
```

The first deployment can take several minutes. Skipping the optional Kudu
warmup avoids waiting on a fresh app whose startup DLL has not been uploaded yet.
Verify the live endpoints below rather than treating upload acceptance as success.

Alternatively use the app's **Development Tools > Advanced Tools** (Kudu)
deployment interface to upload the ZIP where that interface is available.
If organisational access policy blocks CLI sign-in, do not disable the policy;
use an approved deployment method or ask your administrator. This demo was
deployed through portal-authenticated Bash Cloud Shell: choose **No storage
account required**, select the Visual Studio subscription, upload the ZIP using
**Manage files > Upload**, then run the deployment command there using
`drone-swarm.zip` as the source path. Cloud Shell can select a different default
subscription even when launched from the right one, so always pass
`--subscription` explicitly.

Open the app's HTTPS URL and check that the dashboard connects. Verify
`/api/public/snapshot` returns JSON and `/api/plugin/status` returns 401 without
a key. New Minecraft plugin configurations already default `backend.base-url`
to the demo app's HTTPS URL. Existing server configurations are preserved;
update their URL manually if needed. Set `backend.plugin-key` to the matching
secret, then restart the plugin.
The dashboard stays empty until the plugin starts a mission.

Delete the dedicated resource group when finished to stop charges. Stopping
only the web app does **not** stop App Service plan billing.
