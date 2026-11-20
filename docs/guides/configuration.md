# Configuration

`Options<T>(section, bind)` binds one owned section with `bind`, which receives the section, checks that the section
is present and adds `ValidateOnStart`. `Options<T, TValidator>` also registers a validator. A reload of the host's
configuration updates `IOptionsMonitor<T>`. Troupe checks the section, not what the binder does with it: a binder
that ignores the section leaves the defaults. A section can have one owner; a second `Options<T>` for the same
type fails with TRP007. Any other configuration binding of the type's default instance, such as a leftover
`services.Configure<T>(section)`, a `BindConfiguration` or a `section.Bind(options)` in a `Services` callback, fails
`Build` with TRP022, because binding a section twice repeats its collection entries. A binding is an action that enumerates
the owned section or one of its subsections; reading single keys, binding other sections and binding named instances
are not. To find them, `Build` runs each configure and post-configure action of an owned options type once more on a
new instance.

```csharp
component.Options<StorageOptions, StorageOptionsValidator>("Storage", (section, options) => section.Bind(options));
component.ReadsConfiguration("ConnectionStrings:Storage");
component.ReadsConfiguration("Storage:Cache", required: false);
component.ReadsConfigurationUnless<GatewayHost>("Roles:Remote:gateway");
```

`ReadsConfiguration` records a section the component reads itself. `ReadsConfigurationUnless<TComponent>` makes a
section required only when `TComponent` is not composed in this process, such as the address of an owner that runs
elsewhere.

A required section that is absent fails `AddTroupe` with TRP005, naming the component and the section. The
[manifest](manifest.md) records configuration paths and their owners, never values.

## Native AOT

`section.Bind(options)` names the options type at the call, so the configuration binding source generator replaces it
with generated code in a trimmed or Native AOT application. The SDK turns the generator on only in a project that sets
`PublishAot` or `PublishTrimmed`. A class library that holds components sets it itself:

```xml
<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
```

Without it, the publish warns at the library's `Bind` call (IL2026), and the options keep their defaults. With
`IsAotCompatible` also set, the library's own build reports it. Write the binder at each call with the concrete options
type: a generic helper that calls `Bind` for its type parameter binds by reflection.
