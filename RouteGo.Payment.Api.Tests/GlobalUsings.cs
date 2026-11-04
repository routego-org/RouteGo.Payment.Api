// Slice test files (linked in from {Project}/Features/**/*.Tests.cs) carry no using directives —
// this file holds the test project's shared set. The sibling slice skill EXTENDS it as its
// test helpers need more (e.g. Testcontainers, AngleSharp).
global using Xunit;
global using RouteGo.Payment.Api.Common;             // HttpClients, WireJson
