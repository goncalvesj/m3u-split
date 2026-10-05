import { broadcastResponseToMainFrame } from '@azure/msal-browser/redirect-bridge'

void broadcastResponseToMainFrame().catch(() => {
  const status = document.getElementById('auth-status')
  if (status) status.textContent = 'Authentication could not return to M3U Split. Close this window and retry from the original app with popups allowed. Your local playlist remains there.'
})
