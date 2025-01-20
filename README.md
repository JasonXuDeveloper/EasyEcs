# EasyEcs

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/EasyEcs/.github/workflows/dotnet.yml?branch=master)
![license](https://img.shields.io/github/license/JasonXuDeveloper/EasyEcs)

An Entity-Component-System library

## Why?

Sometimes we may want to remove bidirectional dependencies between our code, and ECS is a good way to do this.

This design pattern offers a clean way to separate the data from the logic, and it's also a good way to improve the performance of our game.

## What is ECS?

E - Entity

C - Component

S - System

But what really is it?

Well, as a human, we (entities) live in a world (context), and we have some properties (components). Moreover, we have things to do based on our properties (systems).

## Concepts in EasyEcs

- A `Context` holds several `Entity` instances and some `System` instances. 
- Each `Entity` has some `Component` instances. 
- Each `Component` has only data properties.
- Each `System` can filter lots of `Entity` instances in the same `Context` by their components and operate logics on them.

## Why it removes circular dependencies?

- Only `System` contains logics and none of them should depend on each other. (They should only depend on the filtered entities/components)
- `Component` only contains data properties and no logics. (Again, no way to have dependency)
- `Entity` only contains components. (It is really just a container)


## Why is it fast?

- As a `System` can operate on a batch of entities with components, we can well utilize the cache of the MMU.
- We can also easily parallelize the systems to improve the performance, in a multi-core CPU environment.

## Anything special in EasyEcs?

- We have **priority** for `System`, so you can control the order of systems.
- We have **frequency** for `System`, so you can control the frequency of systems being executed.
- We only allow **asynchronous** interfaces for `System` and `Context`, so our ECS should not block the thread (unless you screw up).
- We have a cool guy who is maintaining this library. (Just kidding)

## Example

Just check out the `EasyEcs.UnitTest` project. I have comments there.

## Documentation

Believe me, one day I will make a website for this and document everything.