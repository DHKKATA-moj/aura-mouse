using System.Net;

namespace AuraMouse.Ui;

/// A computer found on the network that may be showing the pairing code.
internal sealed record Device(string Name, IPAddress Ip);
