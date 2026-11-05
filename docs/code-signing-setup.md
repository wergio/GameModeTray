# Code signing setup (SignPath)

Releases are meant to be signed through the free open source program of
[SignPath Foundation](https://signpath.org). `.github/workflows/release.yml` signs only when
the repository variable `SIGNPATH_ORGANIZATION_ID` is set: until then releases are built
unsigned.

## SignPath side

- A project (its slug goes in `SIGNPATH_PROJECT_SLUG`) linked to the predefined trusted
  build system **GitHub.com**.
- A signing policy with manual approval (its slug goes in `SIGNPATH_SIGNING_POLICY_SLUG`).
- An artifact configuration with the slug `exe`. The workflow uploads the file with
  `actions/upload-artifact`, which zips it, so the root element is `<zip-file>`:

  ```xml
  <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
    <zip-file>
      <pe-file path="GameModeTray.exe" product-name="GameModeTray">
        <authenticode-sign />
      </pe-file>
    </zip-file>
  </artifact-configuration>
  ```

  SignPath Foundation may ask for more metadata restrictions (for example on the product
  version): follow their onboarding instructions. The exe carries
  `ProductName = GameModeTray` and `ProductVersion = <tag without the v>`.

## GitHub side

Settings → Secrets and variables → Actions:

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | API token of a SignPath user with the submitter role |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | SignPath project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | SignPath signing policy slug |

## Each release

Push the tag as usual. The workflow stops at "Sign the exe" until the approver approves the
signing request in SignPath (it waits up to an hour).

Once the first signed release is out, add this line to the README: *Free code signing
provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org)*.
