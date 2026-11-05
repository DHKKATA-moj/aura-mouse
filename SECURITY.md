# Security

Aura Mouse lets one device control another, so it is designed around one question: what can someone
on the same network do? This page says what it protects against, what it doesn't, and how to report a problem.

This design has not been audited by an independent security reviewer.

## Reporting a vulnerability

Please use [GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-security-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository rather than opening a public issue. Include the version, the steps, and what an attacker gains.

## What it's built for

Two or more devices you own on a network you trust, such as home Wi-Fi. It does not use a server, and it only
accepts connections from devices on its own local network, both in the program and in the firewall rule it adds.

Use it on a public or shared network with care: others there can see that it's running and its computer name.

## How it works

| Area | Protection |
| --- | --- |
| Pairing | A 6-digit code, used only through SPAKE2 (2048-bit RFC 3526 group). Nothing sent allows testing guesses offline. |
| Guessing the code | Each try counts. After 5 wrong tries the code stops working until the owner asks for a new one, and a code works only once. |
| After pairing | Each device gets its own random 256-bit key, and the code is no longer involved. |
| Traffic | ECDH P-256 and HKDF, then AES-256-GCM with a counter per direction. Tampering breaks the connection. |
| Saved keys | Windows DPAPI for the current user; Android Keystore on the phone. |
| Remote desktop | Off until turned on. Each new viewer needs the owner's approval, unless the owner chose to trust that device. A banner stays on screen while someone is viewing. |
| Remote desktop code | Both screens show a 6-digit code derived from both keys, with the viewer committing to its key first. Different codes mean something is in the middle, so decline. |
| Connections | Limited per address, with a short deadline and small frames until the other side has proved itself. |
| Received files | Names are cleaned so they can't leave the target folder or hide their real extension. On Windows, files sent, dragged or pasted with Ctrl+V get the "from the internet" mark; files pasted through Explorer's Paste menu may not, because Explorer writes those copies itself. |
| Clipboard | Content marked private by password managers is not shared automatically. |

## What it doesn't protect against

- **A paired device is fully trusted.** It can move the mouse, type, read the shared clipboard, send files, and fetch
  files you've copied. Remove devices you no longer own from the saved list, which cuts them off immediately.
- **A compromised computer.** Malware running as your Windows user can read the same keys the program can.
- **Careless approval.** The name in a remote desktop request is whatever the other device claims. Check the code
  and the address before allowing it.
- **Interference from the same network.** Someone there can't guess the code, but can use up its tries so that new
  devices can't pair until you press New code. They can also claim to refuse a trusted device, so it asks for approval
  again; if a request appears where you expected direct entry, check the code. Anyone who can block traffic can cut
  connections.
- **Network visibility.** Computer names, and the fact that Aura Mouse is running, are visible on the local network.
  Nothing is hidden from someone watching which devices talk to each other.
- **Secure Windows screens.** Input can't reach sign-in and administrator prompts; that is a Windows restriction.

## Builds

Released files aren't code-signed with a commercial certificate, so Windows SmartScreen may warn about them. Compare
the SHA-256 listed with each release before running one, or build from source.
