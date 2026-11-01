# Contributions

Some builders belong to the application, such as Wolverine's options, but several components need to configure
them. A component records a contribution, and the application applies all contributions to that builder type once,
inside its own configuration call:

```csharp
// In a component
component.Contribute<WolverineOptions>(options => options.Discovery.IncludeType(typeof(PlaceOrderHandler)));

// In the application, after AddTroupe
builder.UseWolverine(options => builder.ApplyContributions(options));
```

Contributions run in the composition's order. Registrations a contribution callback adds to the service collection
are owned by its contributor; registrations the adapter makes itself are owned by the application. Removing another
owner's registration fails with TRP003.

`Build` checks that every contribution was applied. A contribution that no adapter applied fails with TRP008, and
applying the same builder type twice fails as well. The [Messaging example](../../examples/Messaging) contributes
Wolverine handlers per role.
