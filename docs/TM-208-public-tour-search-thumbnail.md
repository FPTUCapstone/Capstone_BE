# TM-208 public Tour search thumbnail contract

`GET /api/v1/tours` remains the anonymous, paginated, read-only TM-70 search
endpoint. TM-208 adds one property to every item in `items`:

```json
"thumbnailUrl": "https://res.cloudinary.com/example/image/upload/tour-cover.jpg"
```

The property is always present and is `string | null`. A non-null value is the
stored HTTPS delivery URL of the Tour's **Active primary** image. A public Tour
with no eligible primary image returns `"thumbnailUrl": null`, even when it
has other active images. The first image by `sort_order` is not an automatic
fallback. A deleted primary, a Draft/unpublished Tour's image, and
`catalog.POIPhotos` must not be used.

The Tour's existing TM-70 public predicate still determines which Tours are
listed: Approved and published, with an approved Operator profile and an
active Operator account. The current data model has no separate per-image
approval flag; TM-208 does not invent one. Operator-only media metadata,
Cloudinary public identifiers, upload credentials, and full galleries are
never part of this response.

All existing query parameters, filters, pagination, stable title/ID ordering,
availability fields, status codes, `no-store` response behavior, and
ProblemDetails remain unchanged. Existing clients may ignore the new nullable
property. No client is required to display a thumbnail to continue using the
TM-70 contract.

The implementation obtains the primary URL from the selected page's Tour IDs
in one bounded SQL query inside the existing serializable search snapshot;
it does not query Cloudinary while serving public search.

Decision trace: [TM-208](https://tripmate-capstone.atlassian.net/browse/TM-208)
describes the "first ordered Tour photo", but the approved Tour Catalogue
Enrichment addendum restricts public search to the primary image and forbids
an implicit fallback. The owner confirmed primary-only selection for TM-208
on 2026-09-27.
