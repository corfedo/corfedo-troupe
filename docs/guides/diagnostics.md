# Diagnostics

A failed composition throws `CompositionException`. Its `Diagnostics` carry a stable code, a severity, a message and
the owners involved; its `Manifest` is the snapshot at the failure. Errors fail composition; advisories are listed
in `CompositionManifest.Diagnostics` and do not.

## Services, configuration and contributions

| Code | Meaning | Change |
| --- | --- | --- |
| TRP001 | Advisory: several owners register the same service type | Declare `MultipleAllowed<T>()` if intended |
| TRP002 | A hosted service is registered more than once | Register it in one component |
| TRP003 | An owner removed or replaced another owner's registration | Remove it in its owner, or use `ReplaceForTest` in tests |
| TRP004 | An `ExclusiveService<T>()` slot has several registrations | Keep one registration |
| TRP005 | A required configuration section is missing | Add the named section |
| TRP006 | Advisory: a hosted service registered by a factory has an unknown implementation | Register the type when duplicates matter |
| TRP007 | A second `Options<T>` for the same options type | Let one component own the section |
| TRP008 | A contribution was not applied, or applied twice | Call `ApplyContributions` once in the adapter's configuration |

## Definition, selection and phases

| Code | Meaning | Change |
| --- | --- | --- |
| TRP010 | Missing, duplicate or conflicting role or contributor identity | Give each role one unique `[Role]` name |
| TRP011 | Duplicate or empty profile | Rename the profile or add its roles |
| TRP012 | No roles selected or unknown role names in a definition without profiles, `Roles` set for a definition without roles, or a definition with neither roles nor requirements | Pass `--roles` with declared names, or remove the value |
| TRP013 | No profile selected, unknown profile, `Roles` used with profiles, or `Profile` set for a definition without profiles | Pass one of the listed profiles, or remove the value |
| TRP014 | Dependency cycle between components | Break the listed cycle |
| TRP015 | A declaration after the definition or the contributor was closed | Declare inside `Define` or `Compose` only |
| TRP016 | A call in the wrong phase, such as a test override after `Build` | Move the call before `Build` |
| TRP017 | A callback, rule or startup check threw | See the inner exception |
| TRP018 | Another service provider factory built the host | Remove `ConfigureContainer`, `Host.UseServiceProviderFactory` and `Host.UseDefaultServiceProvider` calls |
| TRP019 | A test replacement matched no registration or several | Narrow it with `owner` or `match` |
| TRP020 | A test root requires a component outside its `withinProfile` | Require components the profile composes |

## ASP.NET Core

| Code | Meaning | Change |
| --- | --- | --- |
| TRP100 | Undeclared or duplicate stage, or endpoints without a `UseRouting` stage | Declare the stage and place `UseRouting` in it |
| TRP101 | Two endpoints share a name | Rename one |
| TRP102 | Advisory: overlapping routes from different owners | Check the routes are intended |
| TRP103 | A requested Kestrel endpoint is not configured, or a configured one is not requested | Match `Kestrel:Endpoints` to the profile |
| TRP104 | A second callback owner for a Kestrel endpoint | Configure the endpoint in one component |
| TRP105 | Recorded middleware or endpoints were never applied | Call `app.UseComposition()` |
| TRP106 | A `UseHttps` overload loaded the Kestrel configuration early | Use one of the overloads listed in [ASP.NET Core](aspnetcore.md) |
