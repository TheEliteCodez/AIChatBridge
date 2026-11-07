# AI Chat Bridge

## What it is

AI Chat Bridge is a standalone .NET 8 Windows application that connects other
programs to browser-based AI chat providers. It provides one local command-line
interface for working with ChatGPT, Gemini, and Claude without requiring each
integrating program to implement separate browser automation.

The project was ported from the original ChromeSidecar implementation in
CryptoDayTraderSuite.

## What it does

AI Chat Bridge can:

- Start and manage a dedicated Chrome instance.
- Connect to existing ChatGPT, Gemini, and Claude conversations.
- Send prompts to a selected provider and return the generated response.
- Reuse open provider pages and browser sessions between requests.
- Start a new conversation when requested.
- Show, hide, or close managed browser tabs without stopping Chrome.
- Report connection, provider readiness, rate-limit, and Google verification states.

The browser remains hidden by default and stays running after a command exits.
This avoids repeatedly starting Chrome, loading provider pages, and signing in
for every request.

## How it works

1. The application starts Chrome with a persistent profile under the local
   application-data directory.
2. Chrome exposes a local Chrome DevTools Protocol endpoint on port `9222`.
3. AI Chat Bridge discovers or opens the provider tab requested by the caller.
4. It uses the provider's normal web interface to enter the prompt.
5. It monitors the page and extracts the completed response from the rendered
   conversation.
6. It returns the response or an explicit failure state to the calling process.

This design uses the providers' web applications rather than their APIs, so
the user must be signed in through the managed Chrome profile. The provider
websites can change their page structure or apply rate limits and verification
challenges; those conditions are reported instead of being bypassed.

## Commands

```text
AIChatBridge.exe --connect [--provider chatgpt|gemini|claude]
AIChatBridge.exe --query "your prompt" [--provider chatgpt|gemini|claude]
AIChatBridge.exe --status
AIChatBridge.exe --show
AIChatBridge.exe --hide
```

The sidecar manages its own Chrome profile under the local application data
directory and uses Chrome's remote debugging endpoint on port 9222.
Chrome stays running between commands so repeated connections do not visibly
open and close the browser. Use `--show` or `--hide` to change visibility.
Use `--start` to launch the hidden persistent browser and `--stop` to close it
explicitly. Queries reuse the loaded provider page instead of opening a fresh
conversation on every request.
Use `--new` to explicitly start a new provider chat, and `--close` to close all
tabs in the managed Chrome instance without stopping Chrome itself.
When a provider is specified, startup opens only that provider's tab. Gemini
Google verification pages are reported as `gemini-google-verification` rather
than retried.

## Building

### Prerequisites

- Windows
- .NET 8 SDK
- Visual Studio 2022 with the .NET desktop development workload, if building
  from Visual Studio
- Google Chrome installed on the machine that will run the application

### Command line

From the project directory, restore dependencies and build the Release
configuration:

```powershell
dotnet restore .\AIChatBridge.csproj
dotnet build .\AIChatBridge.sln --configuration Release
```

The executable and its supporting files are written to:

```text
bin\Release\net8.0-windows\
```

Verify the build by displaying the command help:

```powershell
.\bin\Release\net8.0-windows\AIChatBridge.exe --help
```

### Visual Studio

Open `AIChatBridge.sln`, select the `Release` and `x64` configuration, then
choose **Build > Build Solution**. The output is placed in the same
`bin\Release\net8.0-windows\` directory.
