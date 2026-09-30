Public keys that update feeds must be signed with (`*.pub.pem`, embedded into the app).
`dotnet run --project tools/Skypeek.ReleaseTool -- keygen` creates the signing key and puts its public half here.
A build without any key here never installs or offers updates.
