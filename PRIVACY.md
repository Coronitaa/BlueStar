# Privacy Policy — BlueStar Launcher

**Last updated:** October 4, 2026  
**Developer:** Corøna's Studios  
**Application:** BlueStar Launcher  
**Contact:** [GitHub Issues](https://github.com/Coronitaa/BlueStar/issues)

---

## 1. Introduction

BlueStar Launcher ("BlueStar", "the Application", "we", "our") is a desktop game management and launcher application for Windows. This Privacy Policy explains what information the Application collects, how it is used, and your rights regarding your data.

BlueStar is an open-source project licensed under [CC BY-NC-SA 4.0](https://github.com/Coronitaa/BlueStar/blob/main/LICENSE). You can review the full source code at [github.com/Coronitaa/BlueStar](https://github.com/Coronitaa/BlueStar).

---

## 2. Information We Collect

### 2.1 Information BlueStar Does NOT Collect

BlueStar **does not** collect, transmit, or store any of the following:

- Personal identification information (name, email, phone number, address)
- Financial or payment information
- Location data or GPS coordinates
- Biometric data
- Advertising identifiers
- Usage analytics or telemetry sent to our servers
- Browsing history outside the Application
- Contacts or address book data
- Microphone, camera, or sensor data

### 2.2 Information Stored Locally on Your Device

BlueStar stores the following data **locally on your computer only** (never transmitted to external servers controlled by us):

| Data | Purpose | Location |
|------|---------|----------|
| **Application settings** | Language preference, UI configuration, feature toggles | `%APPDATA%\BlueStar\settings.json` |
| **Game library metadata** | Names, paths, and configurations of managed game instances | `%APPDATA%\BlueStar\` |
| **Catalog database** | Offline copy of game metadata for search and browsing | `%APPDATA%\BlueStar\catalog.sqlite` |
| **Log files** | Diagnostic logs for troubleshooting errors | `%APPDATA%\BlueStar\logs\` |
| **WebView2 browsing data** | Cached web content and cookies from embedded Steam store views | `%APPDATA%\BlueStar\webview\` |
| **API keys** | User-provided API credentials for depot services | `%APPDATA%\BlueStar\settings.json` (encrypted via Windows DPAPI) |

### 2.3 Network Communications

BlueStar communicates with the following **third-party services** when you use specific features:

| Service | When Used | Data Sent | Purpose |
|---------|-----------|-----------|---------|
| **Steam CDN / Steam API** | When downloading game depots or viewing store pages | Steam account session tokens (if logged in), requested depot/manifest IDs | Downloading game content and metadata |
| **DepotBox API** (`depotbox.org`) | When browsing catalog or checking game availability | Search queries, API key | Game availability lookups |
| **GitHub API** (`api.github.com`) | When checking for application updates | Application version number | Update notifications |
| **Steam Store** (via WebView2) | When viewing embedded store pages | Standard web browsing data (cookies, user agent) | Displaying Steam store content inline |
| **DigiCert Timestamp Server** | During build process only (not at runtime) | File hash for timestamping | Code signing timestamp verification |

**Important:** BlueStar does not operate its own backend servers for data collection. All network requests are made directly to the third-party services listed above, and their respective privacy policies apply.

---

## 3. How Your Information Is Used

All locally stored data is used exclusively to:

- **Provide application functionality** — managing your game library, searching the catalog, downloading content
- **Preserve your preferences** — remembering your language, UI settings, and configuration
- **Diagnose errors** — log files help troubleshoot issues when you report bugs
- **Authenticate with third-party services** — API keys and session tokens are used only for their intended service

---

## 4. Data Sharing and Disclosure

BlueStar **does not share, sell, rent, or disclose** your personal data to any third parties.

- We do not serve advertisements
- We do not use analytics services
- We do not profile users
- We do not monetize user data in any way

The only data transmitted externally is what is described in Section 2.3 (Network Communications), which goes directly to the respective third-party services you choose to interact with.

---

## 5. Data Security

- **API keys and credentials** are encrypted using Windows Data Protection API (DPAPI), which ties encryption to your Windows user account
- **All network communications** with third-party services use HTTPS/TLS encryption
- **No data is stored on remote servers** controlled by BlueStar developers
- The application runs entirely locally on your machine with user-level permissions (`PrivilegesRequired=lowest`)

---

## 6. Data Retention and Deletion

All data is stored locally on your device. You have full control:

- **To delete all BlueStar data:** Remove the `%APPDATA%\BlueStar` folder
- **To clear WebView2 browsing data:** Remove the `%APPDATA%\BlueStar\webview` folder
- **To reset settings:** Delete `%APPDATA%\BlueStar\settings.json`
- **Uninstalling BlueStar** via the Windows installer removes application files. AppData may remain — delete manually if desired.

---

## 7. Children's Privacy

BlueStar is not directed at children under the age of 13. We do not knowingly collect personal information from children. Since BlueStar does not collect personal information from any user, this concern does not apply.

---

## 8. Third-Party Services

When you use features that connect to third-party services, those services' privacy policies govern the data they receive:

- **Steam / Valve:** [store.steampowered.com/privacy_agreement](https://store.steampowered.com/privacy_agreement/)
- **GitHub:** [docs.github.com/en/site-policy/privacy-policies](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)
- **DepotBox:** [depotbox.org](https://depotbox.org)

---

## 9. Open Source Transparency

BlueStar is open-source software. You can verify all data handling practices by reviewing the source code:

- **Repository:** [github.com/Coronitaa/BlueStar](https://github.com/Coronitaa/BlueStar)
- **License:** [CC BY-NC-SA 4.0](https://github.com/Coronitaa/BlueStar/blob/main/LICENSE)

Every network request, file operation, and data storage mechanism is visible in the source code.

---

## 10. Changes to This Privacy Policy

We may update this Privacy Policy from time to time. Changes will be posted to this file in the GitHub repository with an updated "Last updated" date. Continued use of BlueStar after changes constitutes acceptance of the updated policy.

---

## 11. Contact Us

If you have questions about this Privacy Policy, please open an issue on our GitHub repository:

- **GitHub Issues:** [github.com/Coronitaa/BlueStar/issues](https://github.com/Coronitaa/BlueStar/issues)

---

*This privacy policy applies to BlueStar Launcher version 1.4.3 and later.*
