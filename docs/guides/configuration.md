# Configuration

`Options<T>(section)` binds one owned section, checks that it is present and adds `ValidateOnStart`.
`Options<T, TValidator>` also registers a validator. A section can have one owner; a second `Options<T>` for the same
type fails with TRP007.

```csharp
component.Options<StorageOptions, StorageOptionsValidator>("Storage");
component.ReadsConfiguration("ConnectionStrings:Storage");
component.ReadsConfiguration("Storage:Cache", required: false);
component.ReadsConfigurationUnless<GatewayHost>("Roles:Remote:gateway");
```

`ReadsConfiguration` records a section the component reads itself. `ReadsConfigurationUnless<TComponent>` makes a
section required only when `TComponent` is not composed in this process, such as the address of an owner that runs
elsewhere.

A required section that is absent fails `AddTroupe` with TRP005, naming the component and the section. The
[manifest](manifest.md) records configuration paths and their owners, never values.
