# UC-02 legacy public document migration

The BE now uploads new operator documents with Cloudinary delivery type
`authenticated`. Rows created by earlier builds may still contain public
`https://res.cloudinary.com/.../upload/...` URLs. The Admin detail endpoint does
not return those URLs, but changing the BE cannot revoke a URL already issued
or cached. Complete this migration before treating the Cloudinary finding as
closed in an environment with existing documents.

On 2026-10-08, the owner confirmed that the six public Cloudinary documents in
the local SQL Server were test-only and authorized their deletion instead of
migration. The six exact `document_id` values (30004, 30005, 40004, 50004,
50005, 50006) were matched against six `upload` assets in the shared Cloudinary
account. All six assets were deleted with CDN invalidation, the six SQL rows
were deleted in one transaction, and each former public URL returned HTTP 404.
The five associated operator profiles and accounts were kept. Nine unrelated
seed/legacy rows with non-Cloudinary hosts were not touched. The opt-in live
smoke test separately verified authenticated PNG/PDF upload, signed download,
public URL denial, and cleanup of its two uniquely named test assets. For any
other environment with legacy public documents, follow the migration steps below.

1. Deploy the BE change that signs Admin document links before changing any
   Cloudinary asset. Deploying the BE first temporarily hides legacy links;
   schedule the remaining steps in the same release window. Renaming assets
   while the old BE is serving Admin detail would break its direct links.
2. Back up `dbo.OperatorDocuments` and inventory rows whose `file_url` does not
   begin with `cloudinary-operator://asset/`:

   ```sql
   SELECT document_id, operator_user_id, file_url
   FROM dbo.OperatorDocuments
   WHERE file_url NOT LIKE 'cloudinary-operator://asset/%';
   ```

   Some development seed rows point to other hosts. Do not attempt to rename
   those with Cloudinary; inspect their provenance separately.
3. For each legacy Cloudinary URL, verify the configured cloud name and the
   resource type (`raw` for PDF, `image` for JPG/PNG). Derive the exact Cloudinary
   public ID from the provider, not merely from a guessed URL substring.
   Quarantine any URL outside the configured operator-document folder.
4. Use Cloudinary's signed **rename** operation on that exact asset with
   `type=upload`, `to_type=authenticated`, `overwrite=false`, and
   `invalidate=true`. Use the same `resource_type` and public ID. Confirm the
   authenticated asset is retrievable only with a signed URL and the old public
   URL fails after CDN invalidation propagates. Cloudinary warns that cached
   copies may remain briefly after rename/invalidation.
5. Update only the matching `dbo.OperatorDocuments.document_id` to the new internal
   reference: `cloudinary-operator://asset/<raw|image>/<pdf|jpg|png>/<URL-encoded-public-id>`.
   Use parameterized SQL inside a transaction after verifying the provider
   rename. Reconcile any rename that succeeded while the SQL update failed.
6. Repeat the inventory query until there are no legacy Cloudinary URLs. In an Admin
   session, open each migrated document and confirm its signed URL expires
   after five minutes. An anonymous request to the Admin detail endpoint must
   return 401 and must not generate a download URL.

Do not copy public legacy URLs into the new reference format without changing
the Cloudinary asset's delivery type: that would hide a link in the API while
leaving the underlying file public.
