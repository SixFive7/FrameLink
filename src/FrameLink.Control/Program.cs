using FrameLink.Control;
using FrameLink.Control.Authentication;

// The whole of the entry point. Everything else is in ControlApp, so the pipeline a frame
// talks to is the same pipeline the tests talk to.
var credential = OperatorCredential.FromEnvironment();
var app = ControlApp.Build(args, credential: credential);

// §3.2: an unconfigured instance starts. It does not throw, does not exit non-zero and does
// not refuse to listen — it comes up and explains itself on every surface, because the
// operator is usually the first person to connect a frame and the frame is how they find out.
if (credential.IsConfigured)
{
    app.Logger.StartingConfigured();
}
else
{
    app.Logger.StartingUnconfigured(credential.Problem);
}

await app.RunAsync();
