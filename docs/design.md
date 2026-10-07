# Basis Package Manager — Design

> **Status: working draft.** This document currently covers *Scope and Purpose*
> (who we are building for, and why). The *High-Level Goal*, *Feature Scope*, and
> *Technical Details* sections are stubbed at the bottom and still to be written.

## Scope and Purpose

To determine if we are building the right thing, we need to clearly explain the scope
and stated audience of the Basis Package Manager.

### Who are the different audiences this document may affect?

There are different groups of people that are interested in Basis, and it's important to
call out what their needs are, as they differ by group. Sometimes, an individual fits
into more than one group.

#### Basis Maintainers

We, as the Basis maintainers, roughly have these goals:

- Improve the project and drive adoption.
- Write open source code for commonly needed functionality of VR games, when in-scope.
- Make it easy for game developers using Basis to upstream their fixes and features.
- Have a centralized place to review PRs and atomically make changes to the code.
- Minimize hassle and friction of the development and release process.
- Allow third-party packages to live outside of the main repo, or even the BasisVR
  GitHub org.
- Make it easy to understand how to use Basis and its features, with batteries-included
  demos, packages, and UGC SDKs (or builders for such).
- Ensure that the framework is modular, such that when a game developer doesn't need some
  functionality or wants to do it differently, the framework is sufficiently modular and
  extensible to enable that, without a fork of the core code.
- Seek to provide and standardize open source features for avatars, worlds, props, and
  other UGC while still allowing game developers total control over which features to
  enable and what policies to support.

#### Game Developers

Game devs need ways to consume the Basis framework to build a game or social VR app.
They need to be able to:

- Add or remove official and community Basis packages, without pulling in the whole
  thing.
- Update Basis without too much pain.
- Leverage version control for their own first-party changes (if they choose to), and
  lock their dependencies.
- Write and introduce their own code (both as assets and packages).
- Tweak (at the code level, not just configuration) Basis packages, which may require
  forking.
- Support user-generated content, by turning on and leveraging underlying functionality
  that the Basis Framework provides.
- Own and control their own application, its content policies, and their own
  infrastructure.
- Set policies and (if they so choose) provide their own SDK for UGC.

#### Authors of User-Generated Content

- With relatively little effort and technical expertise, add support for new games
  and/or platforms to their existing Unity projects for avatars, worlds, and props.
- Avatars are what are most commonly needed by players, so avatars tend to represent
  the majority of UGC by volume of content. Therefore, the workflow/support for porting
  an avatar to have support for Basis must be as streamlined as possible, more so than
  worlds or props.
- Utilize familiar workflows, specifically Unity, Blender, and potentially tooling from
  other ecosystems such as ALCOM.
- No need to familiarize themselves with C# code or version control.

### What does Basis provide today?

Today, Basis provides a GitHub repo that users can download. Users receive a Unity
project that is the Basis Demo Application, and go from there as their entry point to
the various actions and needs that they have.

This fills the needs of each camp of users very poorly:

#### Basis Maintainers (today)

- The maintainers have no easy way to decouple community add-ons from the core codebase.
- The demo application gets confused with the core framework due to it being in the same
  Unity Project. Since the demo application looks like a "budget social VR platform," this
  makes it difficult for us to communicate how and why Basis is not a Platform.
- We have no good way right now of providing non-platform-esque Basis demos.
- Game developers have no incentive to upstream their fixes or features (see below for
  more on this).

#### Game Developers (today)

Devs have no official and/or proven workflow for decoupling their first-party changes
from their Basis dependencies. In practice, they typically just fork Basis.

This has several negative outcomes:

- They find it very difficult to update Basis because their changes are intermingled
  with Basis changes.
- They tend to directly patch Basis in their own repo, and that combined with never
  updating means that it becomes difficult or impossible to upstream fixes to Basis.
- Their starting point for their project is always the Basis SocialVR Demo, which is
  only applicable to a certain type of developer. They have to do more work than
  necessary to go build a different type of application.

#### Authors of User-Generated Content (today)

- Users need to navigate GitHub (something they may have never used before) instead of
  ALCOM or something familiar.
- Because Basis is not a minimal avatar project, they pull in a massive download and
  very complex Unity project full of things they don't care about and don't need. Their
  goal is to build and/or upload an avatar, not develop a social VR application.
- They have no straightforward way of adding the Basis packaging tooling to an existing
  Unity project.
- Once they bring over their avatar into the Basis project, there is no way for them to
  keep changes to it consistent with changes to the original avatar project.

### Who is the Basis Package Manager targeting?

The Basis Package Manager explicitly is targeting **game developers** and not directly
targeting Authors of UGC. We anticipate that the introduction of the Basis Package
Manager will simplify the UGC authorship process, but the audience we are optimizing for
here is the game developer.

#### Why are we not targeting UGC Authors?

This is due to three reasons:

1. We cannot make everyone happy all at once; we need to pick an audience.
2. ALCOM already exists and is a good tool for UGC authors.
3. Game developers are likely to want to own the interface between their game and the
   UGC author, therefore it's likely they will replace any standardized SDK we make with
   their own SDK and UGC workflow anyway.

#### Are we abdicating responsibility to provide good tooling for UGC?

No. We will seek to empower game developers via the package manager to "assemble" the
"plumbing" of their own UGC SDK, by letting the game developer choose the appropriate
combination of features and packages they need. This gives the game developer access to
common (and hopefully standardized) functionality they want in their UGC — things like
jiggle bones, Cilbox, etc.

Eventually we seek to make it possible for your game to load a **.BEE container**, and
for any functionality you have turned off to simply be ignored by your engine. This keeps
the game developer in full control over the features their game supports, without
massively fragmenting the underlying UGC packaging.

A point of terminology, because it changes what we actually commit to building:
**.BEE is a container, not a format.** Today it wraps an opaque loader that happens to be
platform-bound (Unity AssetBundles). Making the *container itself* cross-loadable —
decodable by an engine other than the one that produced it — is a compatibility burden we
do not think earns its keep, so we explicitly de-scope "run any .BEE anywhere."

What we *do* invest in is making the **contents** as specified and independently
decodable as is reasonable:

- **Lean on glTF** (i.e. VRM, and eventually VRM-with-extensions that we would specify)
  as the interchange substrate for avatars wherever we can, rather than inventing a
  parallel opaque encoding. This also meets the **VRM-heavy Japanese UGC ecosystem**
  where it already is. (Pragmatically, any extensions we need may be defined ad hoc first
  and standardized later.)
- **Quarantine what cannot be engine-neutral.** Where data genuinely can't be expressed
  in a specified, independently-decodable way, confine it to a clearly delimited part of
  the container instead of letting it contaminate the whole. Platform-specific artifacts
  — e.g. shader bundles — may stay platform-bound, but must travel with **metadata
  declaring which platforms they apply to**, so a loader can skip what it can't use rather
  than fail.

Cross-engine loading (e.g. Godot) is **not a serious short-term target.** But
asset-loading compatibility is a genuinely useful **long-term** consideration — and the
choices above (specified serialization, a glTF/VRM spine, quarantined platform bits) are
what keep that door open cheaply, instead of forcing an expensive cross-loader later.

The game developer remains responsible for educating UGC authors and providing the SDK to
them, including how they choose to visually expose that SDK in Unity (or embed it in other
applications). The on-disk artifact stays a **.BEE container**, with a strong emphasis on
compatibility across different SDK configurations, and we will provide **hooks at
.BEE-generation time** so the game developer can either expose the file directly to the
UGC author or upload it to infrastructure they control.

In other words: the Basis Package Manager can generate you the *plumbing* for whatever
features you turn on in your SDK — but not the *porcelain* and UX around it.

## High-Level Goal

*TODO — to be written.*

## Feature Scope

*TODO — explicit scope of features to be written.*

## Technical Details

*TODO — to be written.*
