import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';

const component = location.pathname === '/chat'
  ? import('./app/chat').then(module => module.Chat)
  : import('./app/app').then(module => module.App);

void component.then(root => bootstrapApplication(root, appConfig)).catch(error => console.error(error));
