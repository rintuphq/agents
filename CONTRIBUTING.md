# Contributing

Thanks for helping improve RintUp. Bug reports and pull requests are welcome.

Security issues: please do not open a public issue. See [SECURITY.md](SECURITY.md).

## Commit messages

Every commit starts with one of these types, followed by a short summary (at most 72 characters):

| Type | Use for |
| -- | -- |
| `major:` | Breaking change (API, configuration, CLI flags, chart values, resource schema) |
| `minor:` | Backwards compatible new feature |
| `patch:` | Bug fix or small improvement |
| `hotfix:` | Urgent fix for something broken in a released version |

Example: `minor: add http_timing attribute to rintup_monitor`

Co-author, generator or assistant attribution lines are not accepted. The same rule is checked in CI.

To use the template and the local check:

```sh
git config commit.template .gitmessage
git config core.hooksPath .githooks
```
