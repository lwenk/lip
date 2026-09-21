# lip update

Update packages. With no package named, every explicitly installed package is updated to its newest release, and the version transitions are listed before anything changes.

## Synopsis

```shell
lip update [<PACKAGES>] [options]
```

## Arguments

| Argument | Description |
| --- | --- |
| `[<PACKAGES>]` | The packages to update. Accepts the same formats as [`lip install`](./install.md#arguments). Omit to update every explicitly installed package. |

## Options

| Option | Description |
| --- | --- |
| `-n, --dry-run` | Run without making any changes. |
| `--ignore-scripts` | Skip running install and uninstall lifecycle scripts while updating packages. |
