# Document input for landscape drafts

In **Paste to landscape**, attach PDF, Word (`.doc`/`.docx`), Excel (`.xls`/`.xlsx`),
CSV or PNG/JPEG/WebP/GIF images, optionally with pasted text. Choose at most four
attachments: 2 MiB per file and 4 MiB combined. Text is limited to 65,536 characters.
The HTTP request envelope is limited to 8 MiB, including JSON/base64 overhead.

Generate a draft, inspect every proposed asset and relationship, edit asset names
or relationship types, and choose **Accept & Import**. Generation does not write
catalogue facts. Import applies the existing validation and authorization and is
audited. Cancel to discard the draft. Unsupported or invalid input is rejected before
the AI call; generating a valid draft uses the existing `atlas.ai.structure` allowance.
An authenticated principal is required, including when only documents are supplied.

## API and providers

`POST /api/v1/structure/draft` retains `text` and `images` and adds optional `documents`:

```json
{
  "text": "Draft the systems described by this inventory",
  "documents": [{
    "name": "inventory.csv",
    "contentType": "text/csv",
    "contentBase64": "bmFtZSxraW5kCkJpbGxpbmcsYXBwbGljYXRpb24="
  }]
}
```

Document media types: `application/pdf`, `application/msword`,
`application/vnd.openxmlformats-officedocument.wordprocessingml.document`,
`application/vnd.ms-excel`,
`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`, and `text/csv`.
The server validates declared type, base64 and decoded sizes. It does not parse,
execute or unpack documents; the declared type is not a content authenticity check.

The same Fabric AI attachment seam carries opaque bytes with purpose
`structure-documents`; mixed image/document input includes all attachments. A
registered `IAiProviderExtension` must declare support for each media type through
`SupportsAttachment(contentType)`. Its default is false for compatibility with
text-only extensions. Only return true when the configured model and transport
actually handle that format and reject malformed content appropriately.

Current built-in text transports and the text-only Portic adapter cannot consume
documents or images. They return `ai_not_configured` / manual mode for attachment
requests, as does an absent provider. They never silently omit an attachment and
generate a misleading text-only result. A document-capable extension is required
for AI document drafting; this change does not bundle a vendor parser or claim
verified parsing of real documents by a model.

Attachments and filenames are supplied to the configured provider under the existing
AI-module consent. Audit contains only input presence/counts and proposal counts,
not filenames, document bytes, pasted text or generated content. The draft format,
allowance capability and explicit import path are unchanged. Existing text-only
clients remain compatible; image inputs now also receive the count/type/size checks.
